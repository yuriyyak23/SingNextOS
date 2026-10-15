using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip.Virtualization;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Tests.Platform;

public sealed class VirtualizationServiceNestedCreationClosureTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;

    [Theory]
    [InlineData("local-refusal")] [InlineData("no-effect")] [InlineData("query-loss")]
    [InlineData("known-refusal")] [InlineData("invocation-copy")] [InlineData("unknown-invocation-copy")]
    [InlineData("native-history")] [InlineData("copy")] [InlineData("result-throw")]
    [InlineData("query-then-close-loss")]
    public async Task OriginalSessionNestedCreationOwnsNoEffectClosureAndIndependentReclaim(string mode)
    {
        var native=DispatchProxy.Create<ExactNestedDomainConsumerTests.INestedRuntime,ExactNestedDomainConsumerTests.NestedSpy>();
        var spy=(ExactNestedDomainConsumerTests.NestedSpy)(object)native;
        spy.Fault=mode is "unknown-invocation-copy" or "query-then-close-loss" ? "query-loss" : mode;
        var parentRuntime=new NeutralDomainRuntimeFacade();
        var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(parentRuntime,new HybridCpuExecutableChildAdapter(spy.Inner,native)));
        var (process,owner)=TestFixtures.Create(kernel,2981,29810);
        var service=TestFixtures.Create(kernel,2982,29820).Handle;
        var admin=TestFixtures.Create(kernel,2983,29830).Handle;
        var foreign=kernel.AllocateBuffer<byte>(service,32).Value!;foreign.Span[0]=0x71;
        var administration=kernel.MintCapability(new(29830),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(admin,administration,owner,"session-nested",[new(ServiceBudgetDimension.OwnedMemoryBytes,512)]).Value!.ProcessBudget;
        var buffer=kernel.AllocateBuffer<byte>(owner,512).Value!;buffer.Span[0]=0x48;
        var create=kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var parent=kernel.CreateVirtualDomain(owner,create,new(1,4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,parent.Domain,parent.ConfigureCapability).IsSuccess);
        var protocol=VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"session-nested",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(owner,descriptor,[]).Value;
        var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        var client=VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        spy.After=name=>
        {
            if(name!="CreateOriginalNestedChild")return;
            Assert.Equal(KernelError.InvalidTransition,host.ProcessNext().Error);
            if(mode is "known-refusal" or "invocation-copy")Assert.True(kernel.RevokeCapability(parent.ConfigureCapability).IsSuccess);
        };
        if(mode=="result-throw")kernel.SessionInvocationSettlementReservedHook=()=>
        {
            kernel.SessionInvocationSettlementReservedHook=null;
            throw new IOException("Nested result reply lost before publication.");
        };
        var reply=client.CreateNestedAsync(new(parent.Domain,parent.ConfigureCapability,
            new(new(1,mode=="local-refusal" ? 0 : 1024),VirtualDomainAuthorityClass.Lifecycle)));
        if(mode=="result-throw")
        {
            Assert.Throws<IOException>(()=>host.ProcessNext());
            Assert.True(host.HasPendingResult);
            Assert.True(host.AdvancePendingResult().IsSuccess);
            var result=await reply;
            Assert.Equal(VirtualDomainState.Created,kernel.QueryVirtualDomain(owner,result.Authority.Domain).Value);
        }
        else
        {
            Assert.False(host.ProcessNext().IsSuccess);
            await Assert.ThrowsAnyAsync<Exception>(async()=>await reply);
        }
        Assert.Equal(mode=="local-refusal" ? 0 : 1,spy.Calls.GetValueOrDefault("CreateOriginalNestedChild"));
        Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(0x48,buffer.Span[0]); // Result read/cancel does not reclaim independent backing.
        var registry=(EndpointSessionInvocationRegistry)kernel.GetType().GetField("_endpointSessionInvocationRegistry",Private)!.GetValue(kernel)!;
        var records=(IDictionary)registry.GetType().GetField("_ownedRecords",Private)!.GetValue(registry)!;
        var invocation=Assert.Single(records.Values.Cast<object>());
        var originalProperty=invocation.GetType().GetProperty("OriginalVirtualCreate")!;
        var original=originalProperty.GetValue(invocation);
        if(mode=="local-refusal")Assert.True((bool)invocation.GetType().GetProperty("VirtualCreateNeverInvoked")!.GetValue(invocation)!);
        else Assert.NotNull(original);
        if(mode=="no-effect")Assert.True((bool)invocation.GetType().GetProperty("VirtualCreateNoEffect")!.GetValue(invocation)!);
        spy.After=null;
        if(mode is "native-history" or "copy")
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
            Assert.Equal(0,spy.Calls.GetValueOrDefault("CloseOriginalChild"));
        }
        else
        {
            if(mode is "query-loss" or "query-then-close-loss")
            {
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            }
            if(mode=="query-then-close-loss")
            {
                spy.Fault="close-loss";
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalChild"));
                Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
            }
            spy.Fault="exact";
            if(mode is "invocation-copy" or "unknown-invocation-copy")
            {
                originalProperty.SetValue(invocation,typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(original,null));
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                Assert.False((bool)invocation.GetType().GetProperty("VirtualCreateContained")!.GetValue(invocation)!);
                Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
                Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalChild"));
                originalProperty.SetValue(invocation,original);
            }
            var terminated=kernel.TerminateProcess(owner);
            Assert.True(terminated.IsSuccess,terminated.Message+" Teardown="+global::System.Text.Json.JsonSerializer.Serialize(kernel.QueryProcessTeardown(owner).Value)+
                " Calls="+global::System.Text.Json.JsonSerializer.Serialize(spy.Calls));
            Assert.All(kernel.QueryBudget(budget).Value!.Usage,u=>Assert.Equal(0UL,u.Used));
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
            Assert.Equal(mode is "local-refusal" or "no-effect" ? 1 : 2,spy.Calls.GetValueOrDefault("CloseOriginalChild"));
            Assert.Equal(0,parentRuntime.ActiveBindingCount);
        }
        Assert.Equal(mode=="local-refusal" ? 0 : 1,spy.Calls.GetValueOrDefault("CreateOriginalNestedChild"));
        Assert.Equal(0x71,foreign.Span[0]);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==foreign.Handle).State);
        Assert.True(kernel.Processes.Resolve(service).IsSuccess);
    }
}
