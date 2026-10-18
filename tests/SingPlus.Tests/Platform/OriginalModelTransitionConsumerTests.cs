using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip.Virtualization;

namespace SingPlus.Tests.Platform;

public sealed class OriginalModelTransitionConsumerTests
{
    [Theory]
    [InlineData("query")] [InlineData("original")] [InlineData("v1")]
    public async Task CompletedOriginalHistorySurvivesLaterGeneratedTransitionsWithoutChangingLiveState(string rollbackPath)
    {
        var proxy=DispatchProxy.Create<IOriginalModel,OriginalSpy>();var spy=(OriginalSpy)(object)proxy;spy.Target=PlatformVirtualDomainTransition.Configure;
        var kernel=new RuntimeKernel(proxy);var caller=TestFixtures.Create(kernel,3091,30910).Handle;var service=TestFixtures.Create(kernel,3092,30920).Handle;
        var cap=kernel.MintCapability(new(30910),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var domain=kernel.CreateVirtualDomain(caller,cap,new(1,4096)).Value!;
        var buffer=kernel.AllocateBuffer<byte>(caller,64).Value!;buffer.Span[0]=0x48;
        var foreign=kernel.AllocateBuffer<byte>(service,16).Value!;foreign.Span[0]=0x73;
        var protocol=VirtualizationServiceClient.CreateProtocol();var descriptor=kernel.RegisterService(service,"historical-model",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(caller,descriptor,[]).Value;var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        var client=VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session));
        var configure=client.ConfigureAsync(new(domain.Domain,domain.ConfigureCapability));Assert.True(host.ProcessNext().IsSuccess);await configure;
        var request=spy.Request!;var original=spy.Inner.ResolveOriginalVirtualDomainTransition(request);Assert.NotNull(original);
        var start=client.StartAsync(new(domain.Domain,domain.ExecuteCapability));Assert.True(host.ProcessNext().IsSuccess);await start;
        var park=client.ParkAsync(new(domain.Domain,domain.ExecuteCapability));Assert.True(host.ProcessNext().IsSuccess);await park;
        Assert.Same(original,spy.Inner.ResolveOriginalVirtualDomainTransition(request));
        Assert.Null(spy.Inner.ResolveOriginalVirtualDomainTransition(new(request.Lease,request.Transition,request.Incarnation)));
        Assert.Equal(VirtualDomainState.Parked,kernel.QueryVirtualDomain(caller,domain.Domain).Value);
        Assert.Equal(3,spy.Inner.OriginalVirtualDomainTransitionEffectCount);Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);Assert.Equal(0x48,buffer.Span[0]);
        var models=(IDictionary)spy.Inner.GetType().GetField("_virtualDomains",Private)!.GetValue(spy.Inner)!;
        var model=Assert.Single(models.Values.Cast<object>());var revision=model.GetType().GetField("Revision")!;
        var current=revision.GetValue(model);revision.SetValue(model,0UL);
        if(rollbackPath=="query")Assert.Null(spy.Inner.ResolveOriginalVirtualDomainTransition(request));
        if(rollbackPath=="original")Assert.Null(spy.Inner.TransitionOriginalVirtualDomain(new(request.Lease,PlatformVirtualDomainTransition.Resume,request.Incarnation)));
        if(rollbackPath=="v1")Assert.Equal(PlatformAuthorityStatus.NotAccepted,spy.Inner.TransitionVirtualDomain(request.Lease,PlatformVirtualDomainTransition.Resume).Status);
        Assert.Equal(0UL,revision.GetValue(model));Assert.Equal(3,spy.Inner.OriginalVirtualDomainTransitionEffectCount);
        Assert.Equal(0x73,foreign.Span[0]);revision.SetValue(model,current);
        Assert.Same(original,spy.Inner.ResolveOriginalVirtualDomainTransition(request));
        Assert.True(kernel.DestroyVirtualDomain(caller,domain.Domain,domain.ConfigureCapability).IsSuccess);
        Assert.Same(original,spy.Inner.ResolveOriginalVirtualDomainTransition(request));Assert.Equal(1,spy.Inner.RevokeVirtualDomainCallCount);
        Assert.True(kernel.TerminateProcess(caller).IsSuccess);Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        Assert.Equal(0x73,foreign.Span[0]);Assert.True(kernel.TerminateProcess(service).IsSuccess);
    }

    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    public interface IOriginalModel : VirtualizationServiceTransitionClosureTests.IModelProvider,
        IPlatformOriginalVirtualDomainTransitionProvider,IPlatformProviderIncarnationSource { }
    public class OriginalSpy : DispatchProxy
    {
        public readonly HostPlatformAuthorityProvider Inner=new();
        public PlatformVirtualDomainTransition Target;
        public string Fault="exact";
        public PlatformOriginalVirtualDomainTransitionRequest? Request;
        public readonly Dictionary<PlatformVirtualDomainTransition,int> Effects=[];
        public Action? After;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(method!.Name=="ResolveOriginalVirtualDomainTransition" && Fault is "query-loss" or "lost-then-close")throw new IOException("Original transition query lost.");
            var result=method.Invoke(Inner,args);
            if(method.Name=="TransitionOriginalVirtualDomain")
            {
                var request=(PlatformOriginalVirtualDomainTransitionRequest)args![0]!;
                if(result is PlatformOriginalVirtualDomainTransitionObservation {NoEffect:false})Effects[request.Transition]=Effects.GetValueOrDefault(request.Transition)+1;
                if(request.Transition==Target)
                {
                    Request=request;After?.Invoke();
                    if(Fault is "reply-loss" or "query-loss" or "lost-then-close")throw new IOException("Original model transition reply lost after effect.");
                    if(Fault=="copy")return typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(result,null);
                }
            }
            return result;
        }
    }

    [Theory]
    [InlineData("Configure","history-v1")] [InlineData("Configure","history-original")] [InlineData("Configure","forged-noeffect")] [InlineData("Configure","bridge-fact-copy")] [InlineData("Configure","bridge-fact-missing")] [InlineData("Configure","reply-loss")] [InlineData("Park","reply-loss")] [InlineData("Resume","reply-loss")]
    [InlineData("Configure","query-loss")] [InlineData("Park","query-loss")] [InlineData("Resume","query-loss")]
    [InlineData("Configure","invocation-copy")] [InlineData("Park","copy")] [InlineData("Resume","history-loss")]
    [InlineData("Configure","source-loss")]
    public async Task OriginalProducerAndSessionResolveWithoutRepeatingModelEffect(string command,string mode)
    {
        var proxy=DispatchProxy.Create<IOriginalModel,OriginalSpy>();var spy=(OriginalSpy)(object)proxy;
        spy.Target=(PlatformVirtualDomainTransition)(-1);
        var kernel=new RuntimeKernel(proxy);
        var (process,owner)=TestFixtures.Create(kernel,3001,30010);var service=TestFixtures.Create(kernel,3002,30020).Handle;
        var admin=TestFixtures.Create(kernel,3003,30030).Handle;
        var foreign=kernel.AllocateBuffer<byte>(service,32).Value!;foreign.Span[0]=0x73;
        var administrator=kernel.MintCapability(new(30030),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(admin,administrator,owner,"original-model",[new(ServiceBudgetDimension.OwnedMemoryBytes,512)]).Value!.ProcessBudget;
        var buffer=kernel.AllocateBuffer<byte>(owner,512).Value!;
        var create=kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var domain=kernel.CreateVirtualDomain(owner,create,new(1,4096)).Value!;
        if(command!="Configure")Assert.True(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess);
        if(command!="Configure")Assert.True(kernel.StartVirtualDomain(owner,domain.Domain,domain.ExecuteCapability).IsSuccess);
        if(command=="Resume")Assert.True(kernel.ParkVirtualDomain(owner,domain.Domain,domain.ExecuteCapability).IsSuccess);
        spy.Target=Enum.Parse<PlatformVirtualDomainTransition>(command);spy.Fault=mode=="invocation-copy" || mode=="forged-noeffect" || (mode is "history-v1" or "history-original") || mode.StartsWith("bridge-fact-",StringComparison.Ordinal) ? "query-loss" : mode;
        var before=spy.Effects.GetValueOrDefault(spy.Target);
        var protocol=VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"original-model",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(owner,descriptor,[]).Value;
        var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        var client=VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        var capability=command=="Configure" ? domain.ConfigureCapability : domain.ExecuteCapability;
        spy.After=()=>
        {
            Assert.Equal(KernelError.InvalidTransition,host.ProcessNext().Error);
            if(mode=="source-loss")Assert.True(kernel.RevokeCapability(capability).IsSuccess);
            if(mode=="history-loss")
            {
                var domains=(IDictionary)spy.Inner.GetType().GetField("_virtualDomains",Private)!.GetValue(spy.Inner)!;
                var nativeOwner=Assert.Single(domains.Values.Cast<object>());
                ((IDictionary)nativeOwner.GetType().GetField("Transitions")!.GetValue(nativeOwner)!).Clear();
            }
        };
        var request=new VirtualDomainCommand(domain.Domain,capability);
        var reply=command switch{"Configure"=>client.ConfigureAsync(request),"Park"=>client.ParkAsync(request),_=>client.ResumeAsync(request)};
        var dispatched=host.ProcessNext();
        if(mode=="reply-loss"){Assert.True(dispatched.IsSuccess,dispatched.Message);await reply;}
        else{Assert.False(dispatched.IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await reply);}
        Assert.Equal(before+1,spy.Effects.GetValueOrDefault(spy.Target));
        Assert.NotNull(spy.Request);Assert.True(spy.Request!.IsOriginal);
        var equal=new PlatformOriginalVirtualDomainTransitionRequest(spy.Request.Lease,spy.Request.Transition,spy.Request.Incarnation);
        Assert.Null(spy.Inner.ResolveOriginalVirtualDomainTransition(equal));
        var copied=(PlatformOriginalVirtualDomainTransitionRequest)typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(spy.Request,null)!;
        Assert.Null(spy.Inner.ResolveOriginalVirtualDomainTransition(copied));Assert.Null(spy.Inner.TransitionOriginalVirtualDomain(copied));
        Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        spy.After=null;
        if(mode=="history-loss")
        {
            var domains=(IDictionary)spy.Inner.GetType().GetField("_virtualDomains",Private)!.GetValue(spy.Inner)!;
            var nativeOwner=Assert.Single(domains.Values.Cast<object>());
            ((IDictionary)nativeOwner.GetType().GetField("Transitions")!.GetValue(nativeOwner)!).Clear();
        }
        if(mode is "query-loss" or "invocation-copy")
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        spy.Fault="exact";
        if(mode=="forged-noeffect" || (mode is "history-v1" or "history-original"))
        {
            var models=(IDictionary)spy.Inner.GetType().GetField("_virtualDomains",Private)!.GetValue(spy.Inner)!;
            var model=Assert.Single(models.Values.Cast<object>());
            var transitions=(IDictionary)model.GetType().GetField("Transitions")!.GetValue(model)!;
            var fact=Assert.Single(transitions.Values.Cast<object>());var rawField=fact.GetType().GetField("Raw")!;
            var raw=(PlatformOriginalVirtualDomainTransitionObservation)rawField.GetValue(fact)!;
            rawField.SetValue(fact,new PlatformOriginalVirtualDomainTransitionObservation(raw.Request,PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted,"Replaced disposition"),raw.Revision,true));
            var effects=spy.Inner.OriginalVirtualDomainTransitionEffectCount;
            if(mode=="history-v1")Assert.Equal(PlatformAuthorityStatus.NotAccepted,spy.Inner.TransitionVirtualDomain(raw.Request.Lease,PlatformVirtualDomainTransition.Start).Status);
            if(mode=="history-original")Assert.Null(spy.Inner.TransitionOriginalVirtualDomain(new(raw.Request.Lease,PlatformVirtualDomainTransition.Start,raw.Request.Incarnation)));
            Assert.Equal(effects,spy.Inner.OriginalVirtualDomainTransitionEffectCount);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(0x73,foreign.Span[0]);rawField.SetValue(fact,raw);
        }
        if(mode.StartsWith("bridge-fact-",StringComparison.Ordinal))
        {
            var bridgeOwners=(IDictionary)kernel.PlatformAuthority.GetType().GetField("_originalVirtualDomainOwners",Private)!.GetValue(kernel.PlatformAuthority)!;
            var modelOwner=Assert.Single(bridgeOwners.Values.Cast<object>());
            var field=modelOwner.GetType().GetProperty("OriginalTransition")!;var originalFact=field.GetValue(modelOwner)!;
            field.SetValue(modelOwner,mode.EndsWith("missing",StringComparison.Ordinal)?null:typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(originalFact,null));
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);Assert.Equal(0x73,foreign.Span[0]);
            field.SetValue(modelOwner,originalFact);
        }
        if(mode=="invocation-copy")
        {
            var registry=(EndpointSessionInvocationRegistry)kernel.GetType().GetField("_endpointSessionInvocationRegistry",Private)!.GetValue(kernel)!;
            var invocation=Assert.Single(((IDictionary)registry.GetType().GetField("_ownedRecords",Private)!.GetValue(registry)!).Values.Cast<object>());
            var property=invocation.GetType().GetProperty("OriginalVirtualTransitionOwner")!;var original=property.GetValue(invocation)!;
            property.SetValue(invocation,typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(original,null));
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(1,spy.Inner.RevokeVirtualDomainCallCount);
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            property.SetValue(invocation,original);
        }
        var closed=kernel.TerminateProcess(owner);
        if(mode is "copy" or "history-loss")
        {
            Assert.False(closed.IsSuccess);Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        }
        else
        {
            Assert.True(closed.IsSuccess,closed.Message);
            Assert.Equal(1,spy.Inner.RevokeVirtualDomainCallCount);
            Assert.All(kernel.QueryBudget(budget).Value!.Usage,u=>Assert.Equal(0UL,u.Used));
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        }
        Assert.Equal(before+1,spy.Effects.GetValueOrDefault(spy.Target));
        Assert.Equal(0x73,foreign.Span[0]);Assert.True(kernel.Processes.Resolve(service).IsSuccess);
    }
}
