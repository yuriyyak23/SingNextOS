using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip.Virtualization;

namespace SingPlus.Tests.Platform;

public sealed class OriginalModelRevokeConsumerTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    public interface IOriginalRevoke : OriginalModelTransitionConsumerTests.IOriginalModel,IPlatformOriginalVirtualDomainRevokeProvider { }
    public class RevokeSpy : DispatchProxy
    {
        public readonly HostPlatformAuthorityProvider Inner=new();
        public string Fault="exact";
        public bool BadSource;
        public PlatformOriginalVirtualDomainRevokeRequest? Request;
        public Action? After;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(method!.Name=="get_CurrentIncarnation" && BadSource)return new PlatformProviderIncarnation(Inner.CurrentIncarnation.Value+1);
            if(method.Name=="ResolveOriginalVirtualDomainRevoke" && Fault=="query-loss")throw new IOException("Model close lookup lost.");
            if(method.Name=="RevokeOriginalVirtualDomain")
            {
                Request=(PlatformOriginalVirtualDomainRevokeRequest)args![0]!;
                if(Fault=="before-effect")throw new IOException("Close delivery lost before provider admission.");
            }
            var raw=method.Invoke(Inner,args);
            if(method.Name=="RevokeOriginalVirtualDomain")
            {
                After?.Invoke();
                if(Fault is "reply-loss" or "query-loss")throw new IOException("Close reply lost after effect.");
                if(Fault=="copy")return typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(raw,null);
            }
            return raw;
        }
    }
    [Theory]
    [InlineData("reply-loss")] [InlineData("query-loss")] [InlineData("before-effect")]
    [InlineData("copy")] [InlineData("history-loss")] [InlineData("source-loss")] [InlineData("invocation-copy")]
    public async Task OriginalModelClosureControlsIndependentSessionRegionAndChargeReclaim(string mode)
    {
        var proxy=DispatchProxy.Create<IOriginalRevoke,RevokeSpy>();var spy=(RevokeSpy)(object)proxy;
        var kernel=new RuntimeKernel(proxy);
        var (process,owner)=TestFixtures.Create(kernel,3011,30110);var service=TestFixtures.Create(kernel,3012,30120).Handle;
        var admin=TestFixtures.Create(kernel,3013,30130).Handle;
        var foreign=kernel.AllocateBuffer<byte>(service,32).Value!;foreign.Span[0]=0x74;
        var administration=kernel.MintCapability(new(30130),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(admin,administration,owner,"original-close",[new(ServiceBudgetDimension.OwnedMemoryBytes,512)]).Value!.ProcessBudget;
        var buffer=kernel.AllocateBuffer<byte>(owner,512).Value!;
        var create=kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var domain=kernel.CreateVirtualDomain(owner,create,new(1,4096)).Value!;
        var protocol=VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"original-close",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(owner,descriptor,[]).Value;
        var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        var client=VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        var configured=client.ConfigureAsync(new(domain.Domain,domain.ConfigureCapability));
        Assert.True(host.ProcessNext().IsSuccess);await configured;
        var registry=(EndpointSessionInvocationRegistry)kernel.GetType().GetField("_endpointSessionInvocationRegistry",Private)!.GetValue(kernel)!;
        var invocation=Assert.Single(((IDictionary)registry.GetType().GetField("_ownedRecords",Private)!.GetValue(registry)!).Values.Cast<object>());
        var ownerProperty=invocation.GetType().GetProperty("OriginalVirtualTransitionOwner")!;
        var original=ownerProperty.GetValue(invocation)!;
        if(mode=="invocation-copy")ownerProperty.SetValue(invocation,typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(original,null));
        spy.Fault=mode;
        spy.After=()=>
        {
            if(mode=="source-loss")spy.BadSource=true;
            if(mode=="history-loss")
            {
                var records=(IDictionary)spy.Inner.GetType().GetField("_virtualDomains",Private)!.GetValue(spy.Inner)!;
                Assert.Single(records.Values.Cast<object>()).GetType().GetField("Revoke")!.SetValue(Assert.Single(records.Values.Cast<object>()),null);
            }
        };
        var first=kernel.TerminateProcess(owner);
        Assert.NotNull(spy.Request);
        var copied=(PlatformOriginalVirtualDomainRevokeRequest)typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(spy.Request,null)!;
        Assert.Null(spy.Inner.RevokeOriginalVirtualDomain(copied));Assert.Null(spy.Inner.ResolveOriginalVirtualDomainRevoke(copied));
        Assert.Null(spy.Inner.ResolveOriginalVirtualDomainRevoke(new(spy.Request!.Lease,spy.Request.Incarnation)));
        if(mode=="reply-loss")Assert.True(first.IsSuccess,first.Message);
        else
        {
            Assert.False(first.IsSuccess);
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        }
        Assert.Equal(mode=="before-effect" ? 0 : 1,spy.Inner.RevokeVirtualDomainCallCount);
        spy.After=null;
        if(mode is "copy" or "history-loss" or "before-effect")
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        else
        {
            spy.Fault="exact";spy.BadSource=false;
            if(mode=="invocation-copy")ownerProperty.SetValue(invocation,original);
            var closed=mode=="reply-loss" ? first : kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
            Assert.True(spy.Inner.ResolveOriginalVirtualDomainRevoke(spy.Request!)!.NoFurtherDomainUse);
            Assert.All(kernel.QueryBudget(budget).Value!.Usage,u=>Assert.Equal(0UL,u.Used));
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        }
        Assert.Equal(mode=="before-effect" ? 0 : 1,spy.Inner.RevokeVirtualDomainCallCount);
        Assert.Equal(0x74,foreign.Span[0]);Assert.True(kernel.Processes.Resolve(service).IsSuccess);
    }
}
