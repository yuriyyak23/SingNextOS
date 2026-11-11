using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Virtualization;

namespace SingPlus.Tests.Platform;

public sealed class OriginalModelTransitionConsumerTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GeneratedTransitionPublicationRequiresOriginalRawAfterFinalSourceCallback(bool copied)
    {
        IOriginalModel proxy =DispatchProxy.Create<IOriginalModel,OriginalSpy>(); OriginalSpy spy =(OriginalSpy)(object)proxy;
        spy.Target=PlatformVirtualDomainTransition.Configure;
        RuntimeKernel kernel =new RuntimeKernel(proxy); ProcessHandle caller =TestFixtures.Create(kernel,3131,31310).Handle; ProcessHandle service =TestFixtures.Create(kernel,3132,31320).Handle;
        CapabilityId admin =kernel.MintCapability(new(31320),service,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(service,admin,caller,"transition-raw",[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]).Value!.ProcessBudget;
        OwnedBuffer<byte> buffer =kernel.AllocateBuffer<byte>(caller,64).Value!; OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(service,8).Value!;foreign.Span.Fill(0x73);
        CapabilityId cap =kernel.MintCapability(new(31310),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain =kernel.CreateVirtualDomain(caller,cap,new(1,4096)).Value!;
        IDictionary owners =(IDictionary)kernel.PlatformAuthority.GetType().GetField("_originalVirtualDomainOwners",Private)!.GetValue(kernel.PlatformAuthority)!;
        object owner =Assert.Single(owners.Values.Cast<object>());object? fact=null;object? raw=null;
        spy.AfterSource=()=>
        {
            if(owner.GetType().GetProperty("TransitionObservation")!.GetValue(owner) is null)return;
            fact=owner.GetType().GetProperty("OriginalTransition")!.GetValue(owner)!;
            FieldInfo field =fact.GetType().GetField("Raw")!;raw=field.GetValue(fact);Assert.NotNull(raw);
            field.SetValue(fact,copied?typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(raw,null):null);
            spy.AfterSource=null;
        };
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"transition-raw",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(caller,descriptor,[]).Value; RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        ValueTask reply =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session)).ConfigureAsync(new(domain.Domain,domain.ConfigureCapability));
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await reply);
        Assert.NotNull(fact);Assert.NotNull(raw);Assert.Equal(1,spy.Inner.OriginalVirtualDomainTransitionEffectCount);Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x=>x.Handle==buffer.Handle).State);
        Assert.Equal(64UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        fact!.GetType().GetField("Raw")!.SetValue(fact,raw);
        KernelResult closed =kernel.TerminateProcess(caller);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(1,spy.Inner.RevokeVirtualDomainCallCount);
        Assert.Equal(1,spy.Effects[PlatformVirtualDomainTransition.Configure]);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(x=>x.Handle==buffer.Handle).State);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage,x=>Assert.Equal(0UL,x.Used));Assert.All(foreign.Span.ToArray(),x=>Assert.Equal((byte)0x73,x));
    }

    [Theory]
    [InlineData("query")] [InlineData("original")] [InlineData("v1")]
    public async Task CompletedOriginalHistorySurvivesLaterGeneratedTransitionsWithoutChangingLiveState(string rollbackPath)
    {
        IOriginalModel proxy =DispatchProxy.Create<IOriginalModel,OriginalSpy>(); OriginalSpy spy =(OriginalSpy)(object)proxy;spy.Target=PlatformVirtualDomainTransition.Configure;
        RuntimeKernel kernel =new RuntimeKernel(proxy); ProcessHandle caller =TestFixtures.Create(kernel,3091,30910).Handle; ProcessHandle service =TestFixtures.Create(kernel,3092,30920).Handle;
        CapabilityId cap =kernel.MintCapability(new(30910),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain =kernel.CreateVirtualDomain(caller,cap,new(1,4096)).Value!;
        OwnedBuffer<byte> buffer =kernel.AllocateBuffer<byte>(caller,64).Value!;buffer.Span[0]=0x48;
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(service,16).Value!;foreign.Span[0]=0x73;
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol(); ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"historical-model",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(caller,descriptor,[]).Value; RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        IVirtualizationService client =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session));
        ValueTask configure =client.ConfigureAsync(new(domain.Domain,domain.ConfigureCapability));Assert.True(host.ProcessNext().IsSuccess);await configure;
        PlatformOriginalVirtualDomainTransitionRequest request =spy.Request!; PlatformOriginalVirtualDomainTransitionObservation? original =spy.Inner.ResolveOriginalVirtualDomainTransition(request);Assert.NotNull(original);
        ValueTask start =client.StartAsync(new(domain.Domain,domain.ExecuteCapability));Assert.True(host.ProcessNext().IsSuccess);await start;
        ValueTask park =client.ParkAsync(new(domain.Domain,domain.ExecuteCapability));Assert.True(host.ProcessNext().IsSuccess);await park;
        Assert.Same(original,spy.Inner.ResolveOriginalVirtualDomainTransition(request));
        Assert.Null(spy.Inner.ResolveOriginalVirtualDomainTransition(new(request.Lease,request.Transition,request.Incarnation)));
        Assert.Equal(VirtualDomainState.Parked,kernel.QueryVirtualDomain(caller,domain.Domain).Value);
        Assert.Equal(3,spy.Inner.OriginalVirtualDomainTransitionEffectCount);Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);Assert.Equal(0x48,buffer.Span[0]);
        IDictionary models =(IDictionary)spy.Inner.GetType().GetField("_virtualDomains",Private)!.GetValue(spy.Inner)!;
        object model =Assert.Single(models.Values.Cast<object>()); FieldInfo revision =model.GetType().GetField("Revision")!;
        object? current =revision.GetValue(model);revision.SetValue(model,0UL);
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
        public Action? AfterSource;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(method!.Name=="ResolveOriginalVirtualDomainTransition" && Fault is "query-loss" or "lost-then-close")throw new IOException("Original transition query lost.");
            object? result =method.Invoke(Inner,args);
            if(method.Name=="get_CurrentIncarnation")AfterSource?.Invoke();
            if(method.Name=="TransitionOriginalVirtualDomain")
            {
                PlatformOriginalVirtualDomainTransitionRequest request =(PlatformOriginalVirtualDomainTransitionRequest)args![0]!;
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
        IOriginalModel proxy =DispatchProxy.Create<IOriginalModel,OriginalSpy>(); OriginalSpy spy =(OriginalSpy)(object)proxy;
        spy.Target=(PlatformVirtualDomainTransition)(-1);
        RuntimeKernel kernel =new RuntimeKernel(proxy);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel,3001,30010); ProcessHandle service =TestFixtures.Create(kernel,3002,30020).Handle;
        ProcessHandle admin =TestFixtures.Create(kernel,3003,30030).Handle;
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(service,32).Value!;foreign.Span[0]=0x73;
        CapabilityId administrator =kernel.MintCapability(new(30030),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(admin,administrator,owner,"original-model",[new(ServiceBudgetDimension.OwnedMemoryBytes,512)]).Value!.ProcessBudget;
        OwnedBuffer<byte> buffer =kernel.AllocateBuffer<byte>(owner,512).Value!;
        CapabilityId create =kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain =kernel.CreateVirtualDomain(owner,create,new(1,4096)).Value!;
        if(command!="Configure")Assert.True(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess);
        if(command!="Configure")Assert.True(kernel.StartVirtualDomain(owner,domain.Domain,domain.ExecuteCapability).IsSuccess);
        if(command=="Resume")Assert.True(kernel.ParkVirtualDomain(owner,domain.Domain,domain.ExecuteCapability).IsSuccess);
        spy.Target=Enum.Parse<PlatformVirtualDomainTransition>(command);spy.Fault=mode=="invocation-copy" || mode=="forged-noeffect" || (mode is "history-v1" or "history-original") || mode.StartsWith("bridge-fact-",StringComparison.Ordinal) ? "query-loss" : mode;
        int before =spy.Effects.GetValueOrDefault(spy.Target);
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"original-model",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(owner,descriptor,[]).Value;
        RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        IVirtualizationService client =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        CapabilityId capability =command=="Configure" ? domain.ConfigureCapability : domain.ExecuteCapability;
        spy.After=()=>
        {
            Assert.Equal(KernelError.InvalidTransition,host.ProcessNext().Error);
            if(mode=="source-loss")Assert.True(kernel.RevokeCapability(capability).IsSuccess);
            if(mode=="history-loss")
            {
                IDictionary domains =(IDictionary)spy.Inner.GetType().GetField("_virtualDomains",Private)!.GetValue(spy.Inner)!;
                object nativeOwner =Assert.Single(domains.Values.Cast<object>());
                ((IDictionary)nativeOwner.GetType().GetField("Transitions")!.GetValue(nativeOwner)!).Clear();
            }
        };
        VirtualDomainCommand request =new VirtualDomainCommand(domain.Domain,capability);
        ValueTask reply =command switch{"Configure"=>client.ConfigureAsync(request),"Park"=>client.ParkAsync(request),_=>client.ResumeAsync(request)};
        KernelResult dispatched =host.ProcessNext();
        if(mode=="reply-loss"){Assert.True(dispatched.IsSuccess,dispatched.Message);await reply;}
        else{Assert.False(dispatched.IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await reply);}
        Assert.Equal(before+1,spy.Effects.GetValueOrDefault(spy.Target));
        Assert.NotNull(spy.Request);Assert.True(spy.Request!.IsOriginal);
        PlatformOriginalVirtualDomainTransitionRequest equal =new PlatformOriginalVirtualDomainTransitionRequest(spy.Request.Lease,spy.Request.Transition,spy.Request.Incarnation);
        Assert.Null(spy.Inner.ResolveOriginalVirtualDomainTransition(equal));
        PlatformOriginalVirtualDomainTransitionRequest copied =(PlatformOriginalVirtualDomainTransitionRequest)typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(spy.Request,null)!;
        Assert.Null(spy.Inner.ResolveOriginalVirtualDomainTransition(copied));Assert.Null(spy.Inner.TransitionOriginalVirtualDomain(copied));
        Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        spy.After=null;
        if(mode=="history-loss")
        {
            IDictionary domains =(IDictionary)spy.Inner.GetType().GetField("_virtualDomains",Private)!.GetValue(spy.Inner)!;
            object nativeOwner =Assert.Single(domains.Values.Cast<object>());
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
            IDictionary models =(IDictionary)spy.Inner.GetType().GetField("_virtualDomains",Private)!.GetValue(spy.Inner)!;
            object model =Assert.Single(models.Values.Cast<object>());
            IDictionary transitions =(IDictionary)model.GetType().GetField("Transitions")!.GetValue(model)!;
            object fact =Assert.Single(transitions.Values.Cast<object>()); FieldInfo rawField =fact.GetType().GetField("Raw")!;
            PlatformOriginalVirtualDomainTransitionObservation raw =(PlatformOriginalVirtualDomainTransitionObservation)rawField.GetValue(fact)!;
            rawField.SetValue(fact,new PlatformOriginalVirtualDomainTransitionObservation(raw.Request,PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted,"Replaced disposition"),raw.Revision,true));
            int effects =spy.Inner.OriginalVirtualDomainTransitionEffectCount;
            if(mode=="history-v1")Assert.Equal(PlatformAuthorityStatus.NotAccepted,spy.Inner.TransitionVirtualDomain(raw.Request.Lease,PlatformVirtualDomainTransition.Start).Status);
            if(mode=="history-original")Assert.Null(spy.Inner.TransitionOriginalVirtualDomain(new(raw.Request.Lease,PlatformVirtualDomainTransition.Start,raw.Request.Incarnation)));
            Assert.Equal(effects,spy.Inner.OriginalVirtualDomainTransitionEffectCount);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(0x73,foreign.Span[0]);rawField.SetValue(fact,raw);
        }
        if(mode.StartsWith("bridge-fact-",StringComparison.Ordinal))
        {
            IDictionary bridgeOwners =(IDictionary)kernel.PlatformAuthority.GetType().GetField("_originalVirtualDomainOwners",Private)!.GetValue(kernel.PlatformAuthority)!;
            object modelOwner =Assert.Single(bridgeOwners.Values.Cast<object>());
            PropertyInfo field =modelOwner.GetType().GetProperty("OriginalTransition")!; object originalFact =field.GetValue(modelOwner)!;
            field.SetValue(modelOwner,mode.EndsWith("missing",StringComparison.Ordinal)?null:typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(originalFact,null));
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);Assert.Equal(0x73,foreign.Span[0]);
            field.SetValue(modelOwner,originalFact);
        }
        if(mode=="invocation-copy")
        {
            EndpointSessionInvocationRegistry registry =(EndpointSessionInvocationRegistry)kernel.GetType().GetField("_endpointSessionInvocationRegistry",Private)!.GetValue(kernel)!;
            object invocation =Assert.Single(((IDictionary)registry.GetType().GetField("_ownedRecords",Private)!.GetValue(registry)!).Values.Cast<object>());
            PropertyInfo property =invocation.GetType().GetProperty("OriginalVirtualTransitionOwner")!; object original =property.GetValue(invocation)!;
            property.SetValue(invocation,typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(original,null));
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(1,spy.Inner.RevokeVirtualDomainCallCount);
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            property.SetValue(invocation,original);
        }
        KernelResult closed =kernel.TerminateProcess(owner);
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
