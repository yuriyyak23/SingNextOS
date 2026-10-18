using System.Reflection;
using System.Collections;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Tests.Platform;
public sealed class NativeOriginalBindOutcomeTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void NativeCapacityNoEffectRequiresOriginalHistoryAndPreservesForeignRoot(int fault)
    {
        var native=new NeutralDomainRuntimeFacade();var foreign=native.Bind(NeutralDomainProfile.OrdinaryService);
        Assert.True(foreign.IsBound);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        typeof(NeutralDomainRuntimeFacade).GetField("_nextHandle",flags)!.SetValue(native,ulong.MaxValue);
        var request=new NeutralOriginalDomainBindRequest(Guid.NewGuid(),((INeutralOriginalMappingClosureProvider)native).MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        var raw=native.Bind(request);Assert.True(raw.ProvenNoEffect);Assert.False(raw.Observation.IsBound);
        Assert.Equal(raw,native.ResolveOriginalDomainBind(request));
        Assert.Null(native.ResolveOriginalDomainBind(request with {InvocationId=Guid.NewGuid()}));
        Assert.False(native.Bind(request).ProvenNoEffect);Assert.Equal(raw,native.ResolveOriginalDomainBind(request));
        var facts=(IList)typeof(NeutralDomainRuntimeFacade).GetField("_originalBindInvocations",flags)!.GetValue(native)!;
        var original=facts[0]!;
        if(fault==0)facts[0]=typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null);
        else if(fault==1)facts.Clear();
        else original.GetType().GetField("Raw")!.SetValue(original,raw with {Observation=new(false,(NeutralDomainBindDecision)999,default,"Unknown mandatory decision")});
        Assert.Null(native.ResolveOriginalDomainBind(request));
        Assert.Equal(fault==2,native.Bind(request with {InvocationId=Guid.NewGuid()}).ProvenNoEffect);
        Assert.Equal(1,native.ActiveBindingCount);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OriginalNativeCapacityDispositionClosesAcceptedCreateWithoutRepeat(bool lost)
    {
        var proxy=DispatchProxy.Create<ILossRuntime,LossSpy>();var spy=(LossSpy)(object)proxy;spy.Lose=lost;
        typeof(NeutralDomainRuntimeFacade).GetField("_nextHandle",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(spy.Inner,ulong.MaxValue);
        var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(proxy,new YAKSys_Hybrid_CPU.ExecutableAdapter.HybridCpuExecutableChildAdapter()));
        var (process,owner)=TestFixtures.Create(kernel,2961,29610);var service=TestFixtures.Create(kernel,2962,29620).Handle;
        var region=kernel.AllocateBuffer<byte>(owner,64).Value!;var foreign=kernel.AllocateBuffer<byte>(service,32).Value!;
        var cap=kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var protocol=SingPlus.Sip.Virtualization.VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"capacity",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,SingPlus.Sip.Virtualization.VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(owner,descriptor,[]).Value;
        var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        var client=SingPlus.Sip.Virtualization.VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        var pending=client.CreateAsync(new(cap,new(1,4096)));
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await pending);
        if(lost)
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            spy.Lose=false;
        }
        var closed=kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(1,spy.Binds);Assert.Equal(0,spy.Inner.ActiveBindingCount);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==foreign.Handle).State);
        Assert.True(kernel.Processes.Resolve(service).IsSuccess);
    }
    public interface ISourceProvider : IPlatformOriginalAdmissionResolutionProvider,IPlatformFeatureProvider,IPlatformProviderIncarnationSource,IPlatformChildDomainProvider { }
    public class SourceSpy : DispatchProxy
    {
        public HybridCpuPlatformAuthorityProvider Inner=null!;
        public Action? BeforeSource;
        public int Binds;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(method!.Name=="get_CurrentIncarnation"){var action=BeforeSource;BeforeSource=null;action?.Invoke();}
            if(method.Name=="BindDomain")Binds++;
            return method.Invoke(Inner,args);
        }
    }
    [Theory]
    [InlineData(0,false)] [InlineData(1,false)] [InlineData(2,false)]
    [InlineData(0,true)] [InlineData(1,true)] [InlineData(2,true)]
    public async Task ExecutableCreateSourceRefusalRetainsOriginalIntentAndReclaimsCaller(int fault,bool overlap)
    {
        var native=new NeutralDomainRuntimeFacade();
        var proxy=DispatchProxy.Create<ISourceProvider,SourceSpy>();var spy=(SourceSpy)(object)proxy;
        spy.Inner=new(native,new YAKSys_Hybrid_CPU.ExecutableAdapter.HybridCpuExecutableChildAdapter());
        var kernel=new RuntimeKernel(proxy);
        var (process,owner)=TestFixtures.Create(kernel,2951,29510);var service=TestFixtures.Create(kernel,2952,29520).Handle;
        var region=kernel.AllocateBuffer<byte>(owner,64).Value!;var foreign=kernel.AllocateBuffer<byte>(service,32).Value!;
        var cap=kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var protocol=SingPlus.Sip.Virtualization.VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"root-source-refusal",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,SingPlus.Sip.Virtualization.VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(owner,descriptor,[]).Value;
        var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        var client=SingPlus.Sip.Virtualization.VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        spy.BeforeSource=()=>
        {
            const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
            var pending=(IDictionary)typeof(PlatformAuthorityBridge).GetField("_pendingDomainBinds",flags)!.GetValue(kernel.PlatformAuthority)!;
            var original=Assert.Single(pending.Values.Cast<object>());
            Assert.False((bool)original.GetType().GetProperty("EffectMayHaveOccurred")!.GetValue(original)!);
            var request=(PlatformDomainBindRequest)original.GetType().GetProperty("Request")!.GetValue(original)!;
            Assert.NotEqual(Guid.Empty,request.InvocationId);Assert.Equal(new PlatformDomainIdentity(process.DomainId,owner),request.Subject);
            var registry=(EndpointSessionInvocationRegistry)typeof(RuntimeKernel).GetField("_endpointSessionInvocationRegistry",flags)!.GetValue(kernel)!;
            var records=(IDictionary)registry.GetType().GetField("_ownedRecords",flags)!.GetValue(registry)!;
            var record=Assert.Single(records.Values.Cast<object>());
            Assert.Same(original,record.GetType().GetProperty("OriginalVirtualCreate")!.GetValue(record));
            var invocation=(EndpointSessionInvocationHandle)record.GetType().GetProperty("Handle")!.GetValue(record)!;
            Assert.False(registry.ConsumeVirtualCreationNeverInvoked(invocation,service,original).IsSuccess);
            Assert.False(registry.ConsumeVirtualCreationNeverInvoked(invocation,owner,new object()).IsSuccess);
            if(overlap)Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            if(fault==0)throw new IOException("Root source unavailable before invocation");
            if(fault==1)Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
            if(fault==2)Assert.True(kernel.RevokeCapability(cap).IsSuccess);
        };
        var pending=client.CreateAsync(new(cap,new(1,4096)));
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await pending);
        var closed=kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(0,spy.Binds);Assert.Equal(0,native.ActiveBindingCount);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==foreign.Handle).State);
        Assert.True(kernel.Processes.Resolve(service).IsSuccess);
    }
    public interface ILossRuntime : INeutralDomainRuntime,INeutralOriginalDomainCloseProvider,INeutralOriginalBindOutcomeProvider { }
    public class LossSpy : DispatchProxy
    {
        public NeutralDomainRuntimeFacade Inner=new();
        public bool Lose=true;
        public int Binds;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(Lose && method!.Name=="ResolveOriginalDomainBind")throw new IOException("Original Bind query lost");
            var result=method!.Invoke(Inner,args);
            if(method.Name=="Bind"){Binds++;if(Lose)throw new IOException("Original Bind reply lost after effect");}
            return result;
        }
    }
    [Fact]
    public void ActualKernelRootLossRetainsRegionChargeUntilSameOriginalCleanup()
    {
        var native=DispatchProxy.Create<ILossRuntime,LossSpy>();var spy=(LossSpy)(object)native;
        var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(native));
        var (_,owner)=TestFixtures.Create(kernel,2701,27010);var admin=TestFixtures.Create(kernel,2702,27020).Handle;
        var cap=kernel.MintCapability(new(27020),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(admin,cap,owner,"native Bind",[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]).Value!.ProcessBudget;
        var region=kernel.AllocateBuffer<byte>(owner,64).Value!;
        Assert.False(kernel.BindPlatformDomain(owner).IsSuccess);Assert.Equal(1,spy.Inner.ActiveBindingCount);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(1,spy.Binds);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        spy.Lose=false;var closed=kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(1,spy.Binds);Assert.Equal(0,spy.Inner.ActiveBindingCount);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage,u=>Assert.Equal(0UL,u.Used));
        Assert.True(kernel.Processes.Resolve(admin).IsSuccess);
    }

    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public async Task GeneratedCreateLossRequiresOriginalInvocationClosure(bool copiedOwner,bool serviceExit)
    {
        var native=DispatchProxy.Create<ILossRuntime,LossSpy>();var spy=(LossSpy)(object)native;
        var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(native,new YAKSys_Hybrid_CPU.ExecutableAdapter.HybridCpuExecutableChildAdapter()));
        var (process,owner)=TestFixtures.Create(kernel,2801,28010);var service=TestFixtures.Create(kernel,2802,28020).Handle;
        var foreignOwner=TestFixtures.Create(kernel,2803,28030).Handle;
        var independent=kernel.AllocateBuffer<byte>(foreignOwner,16).Value!;independent.Span[0]=0x71;
        var admin=kernel.MintCapability(new(28030),foreignOwner,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(foreignOwner,admin,owner,"root-bind-failure",[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]).Value!.ProcessBudget;
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        var region=kernel.AllocateBuffer<byte>(owner,64).Value!;region.Span[0]=0x48;
        var target=serviceExit?service:owner;
        var cap=kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var protocol=SingPlus.Sip.Virtualization.VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"Create",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,
            SingPlus.Sip.Virtualization.VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(owner,descriptor,[]).Value;
        var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        var client=SingPlus.Sip.Virtualization.VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        var pending=client.CreateAsync(new(cap,new(1,4096)));
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await pending);
        Assert.False(kernel.TerminateProcess(target).IsSuccess);Assert.Equal(1,spy.Binds);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var registry=(EndpointSessionInvocationRegistry)kernel.GetType().GetField("_endpointSessionInvocationRegistry",flags)!.GetValue(kernel)!;
        var records=(IDictionary)registry.GetType().GetField("_ownedRecords",flags)!.GetValue(registry)!;
        var record=Assert.Single(records.Values.Cast<object>());
        var property=record.GetType().GetProperty("OriginalVirtualCreate")!;var original=property.GetValue(record)!;
        var invocation=(EndpointSessionInvocationHandle)record.GetType().GetProperty("Handle")!.GetValue(record)!;
        Assert.False(registry.ConsumeVirtualCreationClosure(invocation,service,original).IsSuccess);
        Assert.False(registry.ConsumeVirtualCreationClosure(invocation,owner,new object()).IsSuccess);
        Assert.False(registry.ConsumeVirtualCreationNeverInvoked(invocation,service).IsSuccess);
        Assert.False(registry.ConsumeVirtualCreationNeverInvoked(invocation,owner).IsSuccess);
        Assert.False(registry.RetireNoEffectVirtualCreationReply(invocation,service).IsSuccess);
        Assert.False(registry.RetireNoEffectVirtualCreationReply(invocation,owner).IsSuccess);
        spy.Lose=false;
        if(copiedOwner)
        {
            property.SetValue(record,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null));
            Assert.False(kernel.TerminateProcess(target).IsSuccess);
            Assert.False((bool)record.GetType().GetProperty("VirtualCreateContained")!.GetValue(record)!);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            property.SetValue(record,original);
        }
        var closed=kernel.TerminateProcess(target);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(1,spy.Binds);Assert.Equal(0,spy.Inner.ActiveBindingCount);
        Assert.Equal(serviceExit?RegionState.Owned:RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        Assert.Equal(serviceExit?64UL:0UL,Used());Assert.Equal(0x71,independent.Span[0]);
        if(serviceExit){Assert.Equal(0x48,region.Span[0]);Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0UL,Used());}
        else Assert.True(kernel.Processes.Resolve(service).IsSuccess);
        Assert.Equal(0x71,independent.Span[0]);Assert.True(kernel.TerminateProcess(foreignOwner).IsSuccess);
    }
}
