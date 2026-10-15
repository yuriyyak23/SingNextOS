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
    [InlineData("Configure","reply-loss")] [InlineData("Park","reply-loss")] [InlineData("Resume","reply-loss")]
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
        spy.Target=Enum.Parse<PlatformVirtualDomainTransition>(command);spy.Fault=mode=="invocation-copy" ? "query-loss" : mode;
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
