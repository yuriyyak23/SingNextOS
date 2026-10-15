using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Virtualization;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Tests.Platform;

public sealed class VirtualizationServiceTransitionClosureTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    public class MappingSpy : ExactNestedDomainConsumerTests.NestedSpy
    {
        public readonly List<string> Results=[];
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            var raw=base.Invoke(method,args);
            if(method!.Name.Contains("Guest"))Results.Add(method.Name+": "+raw);
            return raw;
        }
    }
    public interface IModelProvider : IPlatformAuthorityProvider,IPlatformFeatureProvider,IPlatformVirtualizationProvider,IPlatformResourceProvider { }
    public class ModelSpy : DispatchProxy
    {
        public readonly HostPlatformAuthorityProvider Inner=new();
        public readonly Dictionary<PlatformVirtualDomainTransition,int> Transitions=[];
        public Action<PlatformVirtualDomainTransition>? After;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            var result=method!.Invoke(Inner,args);
            if(method.Name=="TransitionVirtualDomain")
            {
                var transition=(PlatformVirtualDomainTransition)args![1]!;
                Transitions[transition]=Transitions.GetValueOrDefault(transition)+1;
                After?.Invoke(transition);
            }
            return result;
        }
    }

    [Theory]
    [InlineData(false,false)] [InlineData(false,true)] [InlineData(true,false)] [InlineData(true,true)]
    public async Task ConfigureReplyLossRetainsOriginalDomainUntilExactClosure(bool native,bool copied)
    {
        var external=DispatchProxy.Create<ExactNestedDomainConsumerTests.INestedRuntime,ExactNestedDomainConsumerTests.NestedSpy>();
        var spy=(ExactNestedDomainConsumerTests.NestedSpy)(object)external;
        var model=new HostPlatformAuthorityProvider();
        IPlatformAuthorityProvider provider=native ? new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(),new HybridCpuExecutableChildAdapter(spy.Inner,external)) : model;
        var setup=Setup(new RuntimeKernel(provider));var kernel=setup.Kernel;
        kernel.SessionInvocationSettlementReservedHook=()=>
        {
            kernel.SessionInvocationSettlementReservedHook=null;
            Assert.Equal(KernelError.InvalidTransition,setup.Host.ProcessNext().Error);
            throw new IOException("Configure reply lost after local/provider effect.");
        };
        var reply=setup.Client.ConfigureAsync(new(setup.Domain.Domain,setup.Domain.ConfigureCapability));
        Assert.Throws<IOException>(()=>setup.Host.ProcessNext());
        Assert.Equal(VirtualDomainState.Configured,kernel.QueryVirtualDomain(setup.Owner,setup.Domain.Domain).Value);
        var invocation=Invocation(kernel);var property=invocation.GetType().GetProperty("OriginalVirtualTransitionOwner")!;
        var original=property.GetValue(invocation)!;Assert.NotNull(original);
        Retained(setup);
        if(copied)
        {
            property.SetValue(invocation,typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(original,null));
            Assert.False(kernel.TerminateProcess(setup.Owner).IsSuccess);
            Assert.False((bool)invocation.GetType().GetProperty("VirtualTransitionContained")!.GetValue(invocation)!);
            Retained(setup);
            Assert.Equal(1,native ? spy.Calls.GetValueOrDefault("CloseOriginalChild") : model.RevokeVirtualDomainCallCount);
            property.SetValue(invocation,original);
        }
        var closed=kernel.TerminateProcess(setup.Owner);Assert.True(closed.IsSuccess,closed.Message);
        await Assert.ThrowsAnyAsync<Exception>(async()=>await reply);
        Reclaimed(setup);
        Assert.Equal(1,native ? spy.Calls.GetValueOrDefault("CloseOriginalChild") : model.RevokeVirtualDomainCallCount);
    }

    [Theory]
    [InlineData("Configure","local-refusal")] [InlineData("Park","local-refusal")] [InlineData("Resume","local-refusal")]
    [InlineData("Configure","source-refusal")] [InlineData("Park","source-refusal")] [InlineData("Resume","source-refusal")]
    [InlineData("Configure","provider-loss")] [InlineData("Park","provider-loss")] [InlineData("Resume","provider-loss")]
    public async Task OriginalControlTransitionClosureOrHonestUnknownRetention(string command,string mode)
    {
        var proxy=DispatchProxy.Create<IModelProvider,ModelSpy>();var spy=(ModelSpy)(object)proxy;
        var setup=Setup(new RuntimeKernel(proxy));var kernel=setup.Kernel;
        var transition=Enum.Parse<PlatformVirtualDomainTransition>(command);
        if(command!="Configure" || mode=="local-refusal")Assert.True(kernel.ConfigureVirtualDomain(setup.Owner,setup.Domain.Domain,setup.Domain.ConfigureCapability).IsSuccess);
        if(mode!="local-refusal" && command!="Configure")Assert.True(kernel.StartVirtualDomain(setup.Owner,setup.Domain.Domain,setup.Domain.ExecuteCapability).IsSuccess);
        if(mode!="local-refusal" && command=="Resume")Assert.True(kernel.ParkVirtualDomain(setup.Owner,setup.Domain.Domain,setup.Domain.ExecuteCapability).IsSuccess);
        var before=spy.Transitions.GetValueOrDefault(transition);
        var capability=command=="Configure" ? setup.Domain.ConfigureCapability : setup.Domain.ExecuteCapability;
        spy.After=effect=>
        {
            if(effect!=transition)return;
            Assert.Equal(KernelError.InvalidTransition,setup.Host.ProcessNext().Error);
            if(mode=="source-refusal")Assert.True(kernel.RevokeCapability(capability).IsSuccess);
            if(mode=="provider-loss")throw new IOException("Actual model transition effect reply lost.");
        };
        var request=new VirtualDomainCommand(setup.Domain.Domain,capability);
        var reply=command switch {"Configure"=>setup.Client.ConfigureAsync(request),"Park"=>setup.Client.ParkAsync(request),_=>setup.Client.ResumeAsync(request)};
        Assert.False(setup.Host.ProcessNext().IsSuccess);
        await Assert.ThrowsAnyAsync<Exception>(async()=>await reply);
        Assert.Equal(before+(mode=="local-refusal" ? 0 : 1),spy.Transitions.GetValueOrDefault(transition));
        var invocation=Invocation(kernel);
        if(mode=="local-refusal")Assert.True((bool)invocation.GetType().GetProperty("VirtualTransitionNeverInvoked")!.GetValue(invocation)!);
        else Assert.NotNull(invocation.GetType().GetProperty("OriginalVirtualTransitionOwner")!.GetValue(invocation));
        Retained(setup);spy.After=null;
        if(mode=="provider-loss")
        {
            Assert.False(kernel.TerminateProcess(setup.Owner).IsSuccess);
            Assert.False(kernel.TerminateProcess(setup.Owner).IsSuccess);
            Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);
            Retained(setup); // No original model-transition resolver/containment producer yet.
        }
        else
        {
            var closed=kernel.TerminateProcess(setup.Owner);Assert.True(closed.IsSuccess,closed.Message);
            Reclaimed(setup);Assert.Equal(1,spy.Inner.RevokeVirtualDomainCallCount);
        }
        Assert.Equal(before+(mode=="local-refusal" ? 0 : 1),spy.Transitions.GetValueOrDefault(transition));
        Assert.Equal(0x72,setup.Foreign.Span[0]);
    }

    [Theory]
    [InlineData("reply-loss")] [InlineData("query-loss")] [InlineData("before-effect")]
    [InlineData("copy")] [InlineData("source-loss")] [InlineData("invocation-copy")]
    [InlineData("publication-loss")] [InlineData("local-refusal")]
    public async Task DestroyConsumesOriginalInvocationBeforeDomainRetirement(string mode)
    {
        var proxy=DispatchProxy.Create<OriginalModelRevokeConsumerTests.IOriginalRevoke,OriginalModelRevokeConsumerTests.RevokeSpy>();
        var spy=(OriginalModelRevokeConsumerTests.RevokeSpy)(object)proxy;
        var setup=Setup(new RuntimeKernel(proxy));var kernel=setup.Kernel;
        spy.Fault=mode;
        object? original=null;
        spy.After=()=>
        {
            Assert.Equal(KernelError.InvalidTransition,setup.Host.ProcessNext().Error);
            var invocation=Invocation(kernel);
            var property=invocation.GetType().GetProperty("OriginalVirtualTransitionOwner")!;
            original=property.GetValue(invocation);Assert.NotNull(original);
            if(mode=="invocation-copy")property.SetValue(invocation,typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(original,null));
            if(mode=="source-loss")spy.BadSource=true;
        };
        if(mode=="publication-loss")kernel.SessionInvocationSettlementReservedHook=()=>
        {
            kernel.SessionInvocationSettlementReservedHook=null;
            throw new IOException("Destroy response publication lost after exact domain retirement.");
        };
        if(mode=="local-refusal")Assert.True(kernel.RevokeCapability(setup.Domain.ConfigureCapability).IsSuccess);
        var reply=setup.Client.DestroyAsync(new(setup.Domain.Domain,setup.Domain.ConfigureCapability));
        if(mode=="publication-loss")Assert.Throws<IOException>(()=>setup.Host.ProcessNext());
        else
        {
            var dispatched=setup.Host.ProcessNext();
            Assert.Equal(mode=="reply-loss",dispatched.IsSuccess);
        }
        var fact=Invocation(kernel);
        if(mode=="local-refusal")Assert.True((bool)fact.GetType().GetProperty("VirtualTransitionNeverInvoked")!.GetValue(fact)!);
        else Assert.NotNull(fact.GetType().GetProperty("OriginalVirtualTransitionOwner")!.GetValue(fact));
        Retained(setup); // Domain closure never releases independent process memory.
        Assert.Equal(mode is "before-effect" or "local-refusal" ? 0 : 1,spy.Inner.RevokeVirtualDomainCallCount);
        spy.After=null;
        if(mode=="invocation-copy")fact.GetType().GetProperty("OriginalVirtualTransitionOwner")!.SetValue(fact,original);
        spy.BadSource=false;
        if(mode is "query-loss" or "source-loss")spy.Fault="exact";
        var closed=kernel.TerminateProcess(setup.Owner);
        if(mode is "before-effect" or "copy")
        {
            Assert.False(closed.IsSuccess);Retained(setup);
            Assert.False(kernel.TerminateProcess(setup.Owner).IsSuccess);Retained(setup);
        }
        else {Assert.True(closed.IsSuccess,closed.Message);Reclaimed(setup);}
        if(mode=="reply-loss")await reply;
        else await Assert.ThrowsAnyAsync<Exception>(async()=>await reply);
        Assert.Equal(mode=="before-effect" ? 0 : 1,spy.Inner.RevokeVirtualDomainCallCount);
        Assert.Equal(0x72,setup.Foreign.Span[0]);
    }

    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public async Task MappingClosePublicationLossConsumesOnlyOriginalMappingLifetime(bool native,bool refused)
    {
        var external=DispatchProxy.Create<ExactNestedDomainConsumerTests.INestedRuntime,MappingSpy>();
        var spy=(MappingSpy)(object)external;
        IPlatformAuthorityProvider provider=native ? new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(),new HybridCpuExecutableChildAdapter(spy.Inner,external)) : new HostPlatformAuthorityProvider();
        var setup=Setup(new RuntimeKernel(provider));var kernel=setup.Kernel;
        Assert.True(kernel.ConfigureVirtualDomain(setup.Owner,setup.Domain.Domain,setup.Domain.ConfigureCapability).IsSuccess);
        var memory=kernel.MintCapability(new(29910),setup.Owner,ResourceKind.MemoryRegion,CapabilityResourceIds.MemoryRegion(setup.Buffer.Handle.RegionId),CapabilityRights.Map|CapabilityRights.Read).Value!.CapabilityId;
        var mapped=kernel.MapGuestRegion(setup.Owner,setup.Domain.Domain,setup.Domain.MemoryCapability,memory,setup.Buffer.Handle,new(0,512),GuestMemoryAccess.Read);
        Assert.True(mapped.IsSuccess,mapped.Message+string.Join("\n",spy.Results));
        if(refused)Assert.True(kernel.RevokeCapability(setup.Domain.MemoryCapability).IsSuccess);
        else kernel.SessionInvocationSettlementReservedHook=()=>
        {
            kernel.SessionInvocationSettlementReservedHook=null;
            Assert.Equal(KernelError.InvalidTransition,setup.Host.ProcessNext().Error);
            throw new IOException("Mapping Close response lost after original mapping closure.");
        };
        var reply=setup.Client.CloseGuestRegionMappingAsync(new(setup.Domain.Domain,setup.Domain.MemoryCapability,mapped.Value!.Mapping));
        if(refused)Assert.False(setup.Host.ProcessNext().IsSuccess);
        else Assert.Throws<IOException>(()=>setup.Host.ProcessNext());
        var invocation=Invocation(kernel);
        Assert.True((bool)invocation.GetType().GetProperty(refused ? "VirtualTransitionNeverInvoked" : "VirtualArtifactContained")!.GetValue(invocation)!);
        Retained(setup);
        Assert.Equal(VirtualDomainState.Configured,kernel.QueryVirtualDomain(setup.Owner,setup.Domain.Domain).Value);
        var use=kernel.AcquireRegionUse(setup.Owner,setup.Buffer.Handle,RegionUseMode.ExclusiveWrite,new(0,64));
        Assert.Equal(!refused,use.IsSuccess);
        if(use.IsSuccess)Assert.True(kernel.ReleaseRegionUse(setup.Owner,use.Value!.Handle).IsSuccess);
        var calls=spy.Calls.GetValueOrDefault("UnmapOriginalGuest");
        var closed=kernel.TerminateProcess(setup.Owner);Assert.True(closed.IsSuccess,closed.Message);
        await Assert.ThrowsAnyAsync<Exception>(async()=>await reply);
        Reclaimed(setup);
        if(!refused)Assert.Equal(calls,spy.Calls.GetValueOrDefault("UnmapOriginalGuest"));
    }

    private sealed record Context(RuntimeKernel Kernel,ProcessHandle Owner,ProcessHandle Service,BudgetAccountHandle Budget,
        OwnedBuffer<byte> Buffer,OwnedBuffer<byte> Foreign,VirtualDomainAuthoritySet Domain,RuntimeVirtualizationServiceHost Host,IVirtualizationService Client);
    private static Context Setup(RuntimeKernel kernel)
    {
        var (process,owner)=TestFixtures.Create(kernel,2991,29910);var service=TestFixtures.Create(kernel,2992,29920).Handle;
        var admin=TestFixtures.Create(kernel,2993,29930).Handle;
        var foreign=kernel.AllocateBuffer<byte>(service,32).Value!;foreign.Span[0]=0x72;
        var administration=kernel.MintCapability(new(29930),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(admin,administration,owner,"control-transition",[new(ServiceBudgetDimension.OwnedMemoryBytes,512),new(ServiceBudgetDimension.PinnedMappedMemoryBytes,512),new(ServiceBudgetDimension.GuestMemoryBytes,512),new(ServiceBudgetDimension.RegionUses,8)]).Value!.ProcessBudget;
        var buffer=kernel.AllocateBuffer<byte>(owner,512).Value!;
        var create=kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var domain=kernel.CreateVirtualDomain(owner,create,new(1,4096)).Value!;
        var protocol=VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"control-transition",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(owner,descriptor,[]).Value;
        return new(kernel,owner,service,budget,buffer,foreign,domain,RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!,VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session)));
    }
    private static object Invocation(RuntimeKernel kernel)
    {
        var registry=(EndpointSessionInvocationRegistry)kernel.GetType().GetField("_endpointSessionInvocationRegistry",Private)!.GetValue(kernel)!;
        return Assert.Single(((IDictionary)registry.GetType().GetField("_ownedRecords",Private)!.GetValue(registry)!).Values.Cast<object>());
    }
    private static void Retained(Context setup)
    {
        Assert.Equal(512UL,setup.Kernel.QueryBudget(setup.Budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(RegionState.Owned,setup.Kernel.Regions.Snapshot().Single(r=>r.Handle==setup.Buffer.Handle).State);
    }
    private static void Reclaimed(Context setup)
    {
        Assert.All(setup.Kernel.QueryBudget(setup.Budget).Value!.Usage,u=>Assert.Equal(0UL,u.Used));
        Assert.Equal(RegionState.Released,setup.Kernel.Regions.Snapshot().Single(r=>r.Handle==setup.Buffer.Handle).State);
        Assert.Equal(RegionState.Owned,setup.Kernel.Regions.Snapshot().Single(r=>r.Handle==setup.Foreign.Handle).State);
        Assert.Equal(0x72,setup.Foreign.Span[0]);Assert.True(setup.Kernel.Processes.Resolve(setup.Service).IsSuccess);
    }
}
