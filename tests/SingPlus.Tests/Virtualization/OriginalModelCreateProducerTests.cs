using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip.Virtualization;
namespace SingPlus.Tests.Virtualization;

public sealed class OriginalModelCreateProducerTests
{
    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public async Task SourceCallbackCannotReplaceCapturedModelCreateRequest(bool copied,bool afterEffect)
    {
        var proxy=DispatchProxy.Create<IRuntime,Spy>();var spy=(Spy)(object)proxy;spy.Lose=false;spy.Query="exact";
        var kernel=new RuntimeKernel(proxy);var caller=TestFixtures.Create(kernel,3081,30810).Handle;var service=TestFixtures.Create(kernel,3082,30820).Handle;
        var buffer=kernel.AllocateBuffer<byte>(caller,64).Value!;buffer.Span[0]=0x48;
        var foreignOwner=TestFixtures.Create(kernel,3083,30830).Handle;
        var foreign=kernel.AllocateBuffer<byte>(foreignOwner,16).Value!;foreign.Span[0]=0x71;
        var cap=kernel.MintCapability(new(30810),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var protocol=VirtualizationServiceClient.CreateProtocol();var descriptor=kernel.RegisterService(service,"request-source",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(caller,descriptor,[]).Value;var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var changed=0;Action? restore=null;
        spy.AfterSource=()=>
        {
            var pending=(global::System.Collections.IDictionary)kernel.PlatformAuthority.GetType().GetField("_pendingVirtualDomainCreates",flags)!.GetValue(kernel.PlatformAuthority)!;
            if(pending.Count==0)return;
            var fact=Assert.Single(pending.Values.Cast<object>());
            if((bool)fact.GetType().GetProperty("EffectMayHaveOccurred")!.GetValue(fact)!!=afterEffect)return;
            spy.AfterSource=null;changed++;
            var field=fact.GetType().GetField("<OriginalRequest>k__BackingField",flags)!;
            var original=(PlatformOriginalModelCreateRequest)field.GetValue(fact)!;
            restore=()=>field.SetValue(fact,original);
            field.SetValue(fact,copied?typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null):new PlatformOriginalModelCreateRequest(original.Owner,original.Profile));
        };
        var reply=VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session)).CreateAsync(new(cap,new(1,4096)));
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await reply);
        Assert.Equal(1,changed);Assert.Equal(afterEffect?1:0,spy.Inner.CreateVirtualDomainCallCount);Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);
        Assert.Equal(0x48,buffer.Span[0]);Assert.Equal(0x71,foreign.Span[0]);
        if(afterEffect)
        {
            Assert.False(kernel.TerminateProcess(service).IsSuccess);Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
            restore!();var closed=kernel.TerminateProcess(service);Assert.True(closed.IsSuccess,closed.Message);
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
        var provider=new HostPlatformAuthorityProvider();var kernel=new RuntimeKernel(provider);
        var caller=TestFixtures.Create(kernel,3071,30710).Handle;var admin=TestFixtures.Create(kernel,3072,30720).Handle;
        var budgetCap=kernel.MintCapability(new(30720),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(admin,budgetCap,caller,"delivered-owner",[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]).Value!.ProcessBudget;
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        var buffer=kernel.AllocateBuffer<byte>(caller,64).Value!;buffer.Span[0]=0x48;
        var foreign=kernel.AllocateBuffer<byte>(admin,16).Value!;foreign.Span[0]=0x71;
        var cap=kernel.MintCapability(new(30710),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var domain=kernel.CreateVirtualDomain(caller,cap,new(1,4096)).Value!;
        var other=kernel.CreateVirtualDomain(caller,cap,new(1,4096)).Value!;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var bridge=kernel.PlatformAuthority;
        var owners=(global::System.Collections.IDictionary)bridge.GetType().GetField("_originalVirtualDomainOwners",flags)!.GetValue(bridge)!;
        var routes=(global::System.Collections.IDictionary)bridge.GetType().GetField("_virtualDomainBindings",flags)!.GetValue(bridge)!;
        var key=owners.Keys.Cast<object>().OrderBy(k=>((PlatformAuthorityBridge.VirtualDomainBindingId)k).Value).First();
        var original=owners[key]!;var copy=typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null)!;
        owners[key]=copy;routes[key]=copy;
        var refused=destroy?kernel.DestroyVirtualDomain(caller,domain.Domain,domain.ConfigureCapability):kernel.ConfigureVirtualDomain(caller,domain.Domain,domain.ConfigureCapability);
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
        var proxy=DispatchProxy.Create<IRuntime,Spy>();var spy=(Spy)(object)proxy;var kernel=new RuntimeKernel(proxy);
        var caller=TestFixtures.Create(kernel,3061,30610).Handle;var admin=TestFixtures.Create(kernel,3062,30620).Handle;
        var foreignCap=kernel.MintCapability(new(30620),admin,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        spy.Lose=false;spy.Query="exact";var live=kernel.CreateVirtualDomain(admin,foreignCap,new(1,4096)).Value!;
        var budgetCap=kernel.MintCapability(new(30620),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(admin,budgetCap,caller,"recovered-owner",[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]).Value!.ProcessBudget;
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        var buffer=kernel.AllocateBuffer<byte>(caller,64).Value!;buffer.Span[0]=0x48;
        var foreign=kernel.AllocateBuffer<byte>(admin,16).Value!;foreign.Span[0]=0x71;
        var cap=kernel.MintCapability(new(30610),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        spy.Lose=true;spy.Query="lost";Assert.False(kernel.CreateVirtualDomain(caller,cap,new(1,4096)).IsSuccess);
        spy.Query="exact";spy.AfterRevoke=()=>{spy.AfterRevoke=null;spy.ThrowSource=true;};
        Assert.False(kernel.TerminateProcess(caller).IsSuccess);Assert.Equal(1,spy.Inner.RevokeVirtualDomainCallCount);Assert.Equal(64UL,Used());
        spy.ThrowSource=false;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var bridge=kernel.PlatformAuthority;
        var owners=(global::System.Collections.IDictionary)bridge.GetType().GetField("_originalVirtualDomainOwners",flags)!.GetValue(bridge)!;
        var routes=(global::System.Collections.IDictionary)bridge.GetType().GetField("_virtualDomainBindings",flags)!.GetValue(bridge)!;
        var key=owners.Keys.Cast<object>().Single(k=>((PlatformAuthorityBridge.VirtualDomainBinding)owners[k]!.GetType().GetProperty("Binding")!.GetValue(owners[k])!).Owner==caller);
        var original=owners[key]!;var foreignModel=owners.Values.Cast<object>().Single(v=>!ReferenceEquals(v,original));
        if(fault=="missing"){owners.Remove(key);routes.Remove(key);}
        else {var replacement=fault=="copy"?typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null):foreignModel;owners[key]=replacement;routes[key]=replacement;}
        Assert.False(kernel.TerminateProcess(caller).IsSuccess);Assert.Equal(64UL,Used());Assert.Equal(1,spy.Inner.RevokeVirtualDomainCallCount);
        if(fault=="missing"){Assert.False(owners.Contains(key));Assert.False(routes.Contains(key));}
        Assert.Equal(0x71,foreign.Span[0]);Assert.True(kernel.ConfigureVirtualDomain(admin,live.Domain,live.ConfigureCapability).IsSuccess);
        owners[key]=original;routes[key]=original;
        var closed=kernel.TerminateProcess(caller);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(2,spy.Inner.CreateVirtualDomainCallCount);Assert.Equal(1,spy.Inner.RevokeVirtualDomainCallCount);
        Assert.Equal(0UL,Used());Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        Assert.Equal(0x71,foreign.Span[0]);Assert.True(kernel.TerminateProcess(admin).IsSuccess);Assert.Equal(2,spy.Inner.RevokeVirtualDomainCallCount);
    }

    [Theory]
    [InlineData("raw-copy")] [InlineData("raw-foreign")] [InlineData("missing")] [InlineData("fact-copy")] [InlineData("fact-foreign")]
    public async Task GeneratedCleanupRejectsReplacedProviderRawAndPreservesSameCallerLiveModel(string fault)
    {
        var proxy=DispatchProxy.Create<IRuntime,Spy>();var spy=(Spy)(object)proxy;var kernel=new RuntimeKernel(proxy);
        var caller=TestFixtures.Create(kernel,3051,30510).Handle;var service=TestFixtures.Create(kernel,3052,30520).Handle;
        var foreignOwner=TestFixtures.Create(kernel,3053,30530).Handle;
        var buffer=kernel.AllocateBuffer<byte>(caller,64).Value!;buffer.Span[0]=0x48;
        var foreign=kernel.AllocateBuffer<byte>(foreignOwner,16).Value!;foreign.Span[0]=0x71;
        var cap=kernel.MintCapability(new(30510),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        spy.Lose=false;spy.Query="exact";var live=kernel.CreateVirtualDomain(caller,cap,new(1,4096)).Value!;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var facts=(global::System.Collections.IDictionary)spy.Inner.GetType().GetField("_modelCreates",flags)!.GetValue(spy.Inner)!;
        var liveFact=Assert.Single(facts.Values.Cast<object>());
        var liveRaw=(PlatformOriginalModelCreateObservation)liveFact.GetType().GetField("Raw")!.GetValue(liveFact)!;
        var protocol=VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"raw-custody",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(caller,descriptor,[]).Value;var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        spy.Lose=true;spy.Query="lost";
        var reply=VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session)).CreateAsync(new(cap,new(1,4096)));
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await reply);
        var fact=facts.Values.Cast<object>().Single(f=>!ReferenceEquals(f,liveFact));var field=fact.GetType().GetField("Raw")!;
        var original=(PlatformOriginalModelCreateObservation)field.GetValue(fact)!;
        if(fault=="missing")facts.Remove(original.Request);
        else if(fault=="fact-copy")facts[original.Request]=typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(fact,null);
        else if(fault=="fact-foreign")facts[original.Request]=liveFact;
        else field.SetValue(fact,fault=="raw-foreign"?original with {Outcome=liveRaw.Outcome}:original with {});
        spy.Lose=false;spy.Query="exact";
        var otherCap=kernel.MintCapability(new(30530),foreignOwner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        Assert.False(kernel.CreateVirtualDomain(foreignOwner,otherCap,new(1,4096)).IsSuccess);
        var legacy=spy.Inner.CreateVirtualDomain(original.Request.Owner,original.Request.Profile);
        Assert.Equal(PlatformAuthorityStatus.NotAccepted,legacy.Status);
        Assert.Equal(2,spy.Inner.CreateVirtualDomainCallCount);
        spy.Query="exact";Assert.False(kernel.TerminateProcess(service).IsSuccess);
        Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);Assert.Equal(0x48,buffer.Span[0]);Assert.Equal(0x71,foreign.Span[0]);
        Assert.True(kernel.ConfigureVirtualDomain(caller,live.Domain,live.ConfigureCapability).IsSuccess);
        facts[original.Request]=fact;field.SetValue(fact,original);var closed=kernel.TerminateProcess(service);Assert.True(closed.IsSuccess,closed.Message);
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
        var proxy=DispatchProxy.Create<IRuntime,Spy>();var spy=(Spy)(object)proxy;var kernel=new RuntimeKernel(proxy);
        var caller=TestFixtures.Create(kernel,3041,30410).Handle;var foreignOwner=TestFixtures.Create(kernel,3042,30420).Handle;
        var foreignCap=kernel.MintCapability(new(30420),foreignOwner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        spy.Lose=false;spy.Query="exact";var live=kernel.CreateVirtualDomain(foreignOwner,foreignCap,new(1,4096)).Value!;
        var foreign=kernel.AllocateBuffer<byte>(foreignOwner,16).Value!;foreign.Span[0]=0x71;
        var budgetCap=kernel.MintCapability(new(30420),foreignOwner,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(foreignOwner,budgetCap,caller,"source-substitution",[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]).Value!.ProcessBudget;
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        var buffer=kernel.AllocateBuffer<byte>(caller,64).Value!;buffer.Span[0]=0x48;
        if(noEffect)typeof(HostPlatformAuthorityProvider).GetField("_nextVirtualDomainId",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(spy.Inner,ulong.MaxValue);
        spy.Lose=true;spy.Query="lost";
        var cap=kernel.MintCapability(new(30410),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        Assert.False(kernel.CreateVirtualDomain(caller,cap,new(1,4096)).IsSuccess);
        if(noEffect)typeof(HostPlatformAuthorityProvider).GetField("_nextVirtualDomainId",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(spy.Inner,2UL);
        var source=spy.Inner.CurrentIncarnation;
        spy.Query="exact";spy.AfterQuery=()=>{spy.AfterQuery=null;spy.SourceOverride=new(source.Value==ulong.MaxValue?1:source.Value+1);};
        Assert.False(kernel.TerminateProcess(caller).IsSuccess);Assert.Equal(64UL,Used());Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        Assert.Equal(0x71,foreign.Span[0]);Assert.True(kernel.QueryVirtualDomain(foreignOwner,live.Domain).IsSuccess);
        spy.SourceOverride=null;Assert.True(kernel.ConfigureVirtualDomain(foreignOwner,live.Domain,live.ConfigureCapability).IsSuccess);
        var closed=kernel.TerminateProcess(caller);Assert.True(closed.IsSuccess,closed.Message);
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
        var proxy=DispatchProxy.Create<IRuntime,Spy>();var spy=(Spy)(object)proxy;var kernel=new RuntimeKernel(proxy);
        if(noEffect)typeof(HostPlatformAuthorityProvider).GetField("_nextVirtualDomainId",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(spy.Inner,ulong.MaxValue);
        var caller=TestFixtures.Create(kernel,3031,30310).Handle;var admin=TestFixtures.Create(kernel,3032,30320).Handle;
        var budgetCap=kernel.MintCapability(new(30320),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(admin,budgetCap,caller,"model-direct",[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]).Value!.ProcessBudget;
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        var buffer=kernel.AllocateBuffer<byte>(caller,64).Value!;buffer.Span[0]=0x48;
        var foreign=kernel.AllocateBuffer<byte>(admin,16).Value!;foreign.Span[0]=0x71;
        var cap=kernel.MintCapability(new(30310),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        Assert.False(kernel.CreateVirtualDomain(caller,cap,new(1,4096)).IsSuccess);
        if(copiedFact)
        {
            var routes=(global::System.Collections.IDictionary)kernel.PlatformAuthority.GetType().GetField("_unresolvedVirtualDomainCreates",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
            var key=Assert.Single(routes.Keys.Cast<object>());var original=routes[key]!;routes.Remove(key);
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
            var unresolved=(global::System.Collections.IDictionary)kernel.PlatformAuthority.GetType().GetField("_unresolvedVirtualDomainCreates",flags)!.GetValue(kernel.PlatformAuthority)!;
            var key=Assert.Single(unresolved.Keys.Cast<object>());var original=unresolved[key]!;
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
            var subject=new PlatformDomainIdentity(new(30310),caller);
            var worker=Task.Run(()=>kernel.PlatformAuthority.CloseUnresolvedModelCreations(subject));
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
        var closed=kernel.TerminateProcess(caller);Assert.True(closed.IsSuccess,closed.Message);
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
            var result=method.Invoke(Inner,args);
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
        var proxy=DispatchProxy.Create<IRuntime,Spy>();var spy=(Spy)(object)proxy;var kernel=new RuntimeKernel(proxy);
        if(noEffect)typeof(HostPlatformAuthorityProvider).GetField("_nextVirtualDomainId",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(spy.Inner,ulong.MaxValue);
        var caller=TestFixtures.Create(kernel,3021,30210).Handle;var service=TestFixtures.Create(kernel,3022,30220).Handle;
        var foreignOwner=TestFixtures.Create(kernel,3023,30230).Handle;
        var buffer=kernel.AllocateBuffer<byte>(caller,64).Value!;buffer.Span[0]=0x48;
        var foreign=kernel.AllocateBuffer<byte>(foreignOwner,16).Value!;foreign.Span[0]=0x71;
        var cap=kernel.MintCapability(new(30210),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var protocol=VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"original-model",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(caller,descriptor,[]).Value;var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        var reply=VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session)).CreateAsync(new(cap,new(1,4096)));
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await reply);
        Assert.False(kernel.TerminateProcess(service).IsSuccess);Assert.Equal(1,spy.Inner.CreateVirtualDomainCallCount);Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);
        if(copy){spy.Query="copy";Assert.False(kernel.TerminateProcess(service).IsSuccess);Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);}
        spy.Query="exact";var closed=kernel.TerminateProcess(service);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(1,spy.Inner.CreateVirtualDomainCallCount);Assert.Equal(noEffect?0:1,spy.Inner.RevokeVirtualDomainCallCount);
        Assert.Equal(0x48,buffer.Span[0]);Assert.Equal(0x71,foreign.Span[0]);
        Assert.True(kernel.TerminateProcess(caller).IsSuccess);Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        Assert.Equal(0x71,foreign.Span[0]);Assert.True(kernel.TerminateProcess(foreignOwner).IsSuccess);
    }
}
