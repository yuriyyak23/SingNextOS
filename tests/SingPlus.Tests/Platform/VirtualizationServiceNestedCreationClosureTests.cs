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
    private sealed class LifetimeClock : TimeProvider
    {
        internal Action? Observe;
        public override DateTimeOffset GetUtcNow(){Observe?.Invoke();return base.GetUtcNow();}
    }

    [Theory]
    [InlineData("failed-create-service-exit")] [InlineData("failed-create-handoff-service-exit")] [InlineData("local-refusal")] [InlineData("no-effect")] [InlineData("query-loss")]
    [InlineData("known-refusal")] [InlineData("invocation-copy")] [InlineData("unknown-invocation-copy")]
    [InlineData("native-history")] [InlineData("copy")] [InlineData("result-throw")]
    [InlineData("query-then-close-loss")] [InlineData("result-route-loss")] [InlineData("result-route-service-exit")] [InlineData("result-route-unpublished-service-exit")] [InlineData("result-route-unpublished-close-loss-service-exit")] [InlineData("result-route-unpublished-copy-service-exit")] [InlineData("result-route-unpublished-missing-service-exit")]
    public async Task OriginalSessionNestedCreationOwnsNoEffectClosureAndIndependentReclaim(string mode)
    {
        var native=DispatchProxy.Create<ExactNestedDomainConsumerTests.INestedRuntime,ExactNestedDomainConsumerTests.NestedSpy>();
        var spy=(ExactNestedDomainConsumerTests.NestedSpy)(object)native;
        spy.Fault=mode is "failed-create-service-exit" or "failed-create-handoff-service-exit" or "unknown-invocation-copy" or "query-then-close-loss" ? "query-loss" : mode;
        var parentRuntime=new NeutralDomainRuntimeFacade();
        var routeResult=mode.StartsWith("result-route-",StringComparison.Ordinal);
        var clock=routeResult?new LifetimeClock():null;
        var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(parentRuntime,new HybridCpuExecutableChildAdapter(spy.Inner,native)),clock);
        var (process,owner)=TestFixtures.Create(kernel,2981,29810);
        var service=TestFixtures.Create(kernel,2982,29820).Handle;
        var admin=TestFixtures.Create(kernel,2983,29830).Handle;
        var foreignOwner=mode.EndsWith("service-exit",StringComparison.Ordinal)?TestFixtures.Create(kernel,2984,29840).Handle:service;
        var foreign=kernel.AllocateBuffer<byte>(foreignOwner,32).Value!;foreign.Span[0]=0x71;
        var administration=kernel.MintCapability(new(29830),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(admin,administration,owner,"session-nested",[new(ServiceBudgetDimension.OwnedMemoryBytes,512)]).Value!.ProcessBudget;
        var buffer=kernel.AllocateBuffer<byte>(owner,512).Value!;buffer.Span[0]=0x48;
        var create=kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var parent=kernel.CreateVirtualDomain(owner,create,new(1,4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,parent.Domain,parent.ConfigureCapability).IsSuccess);
        var protocol=VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"session-nested",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(owner,descriptor,[],lifetime:clock is null?null:TimeSpan.FromMinutes(1)).Value;
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
        if(mode=="result-throw" || routeResult)
        {
            if(mode=="result-throw")Assert.Throws<IOException>(()=>host.ProcessNext());
            else
            {
                var invocations=kernel.GetType().GetField("_endpointSessionInvocationRegistry",Private)!.GetValue(kernel)!;
                var routes=(IDictionary)invocations.GetType().GetField("_records",Private)!.GetValue(invocations)!;
                var key=Assert.Single(routes.Keys.Cast<object>());var captured=routes[key]!;var observed=0;
                clock!.Observe=()=>
                {
                    if(host.GetType().GetField("_pendingResult",Private)!.GetValue(host) is null)return;
                    clock.Observe=null;observed++;routes.Remove(key);
                };
                Assert.False(host.ProcessNext().IsSuccess);Assert.Equal(1,observed);Assert.False(reply.IsCompleted);
                Assert.Equal(1,spy.Calls.GetValueOrDefault("CreateOriginalNestedChild"));
                Assert.Equal(0,spy.Calls.GetValueOrDefault("CloseOriginalChild"));
                Assert.False(host.AdvancePendingResult().IsSuccess);
                Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                Assert.Equal(0x48,buffer.Span[0]);routes[key]=captured;
                if(mode.Contains("unpublished",StringComparison.Ordinal))
                {
                    var pending=host.GetType().GetField("_pendingResult",Private)!.GetValue(host)!;
                    var child=Assert.IsType<VirtualDomainAuthorityResponse>(pending.GetType().GetField("Payload",Private)!.GetValue(pending)).Authority;
                    if(mode.Contains("close-loss",StringComparison.Ordinal))
                    {
                        spy.Fault="close-loss";
                        Assert.False(kernel.TerminateProcess(service).IsSuccess);
                        Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalChild"));
                        Assert.False(host.AdvancePendingResult().IsSuccess);
                        Assert.True(host.HasPendingResult);Assert.Equal(0x48,buffer.Span[0]);Assert.Equal(0x71,foreign.Span[0]);
                        Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                        spy.Fault="exact";
                    }
                    if(mode.Contains("copy",StringComparison.Ordinal) || mode.Contains("missing",StringComparison.Ordinal))
                    {
                        var property=captured.GetType().GetProperty("OriginalVirtualCreate")!;
                        var capturedOriginal=property.GetValue(captured)!;
                        property.SetValue(captured,mode.Contains("missing",StringComparison.Ordinal)?null:typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(capturedOriginal,null));
                        Assert.False(kernel.TerminateProcess(service).IsSuccess);
                        Assert.Equal(0,spy.Calls.GetValueOrDefault("CloseOriginalChild"));
                        Assert.True(host.HasPendingResult);Assert.Equal(0x48,buffer.Span[0]);Assert.Equal(0x71,foreign.Span[0]);
                        property.SetValue(captured,capturedOriginal);
                    }
                    var exited=kernel.TerminateProcess(service);Assert.True(exited.IsSuccess,exited.Message+" Drain="+host.Drain().Message+" Calls="+global::System.Text.Json.JsonSerializer.Serialize(spy.Calls));
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await reply);
                    Assert.False(host.HasPendingResult);Assert.Equal(1,spy.Calls.GetValueOrDefault("CreateOriginalNestedChild"));
                    Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalChild"));
                    Assert.False(kernel.QueryVirtualDomain(owner,child.Domain).IsSuccess);
                    Assert.Equal(VirtualDomainState.Configured,kernel.QueryVirtualDomain(owner,parent.Domain).Value);
                    Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                    Assert.Equal(0x48,buffer.Span[0]);Assert.Equal(0x71,foreign.Span[0]);
                    Assert.True((bool)captured.GetType().GetProperty("VirtualCreateContained")!.GetValue(captured)!);
                    Assert.False((bool)captured.GetType().GetProperty("VirtualCreateNoEffect")!.GetValue(captured)!);
                    var ended=kernel.TerminateProcess(owner);Assert.True(ended.IsSuccess,ended.Message);
                    Assert.Equal(2,spy.Calls.GetValueOrDefault("CloseOriginalChild"));
                    Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                    Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
                    Assert.Equal(0,parentRuntime.ActiveBindingCount);Assert.Equal(0x71,foreign.Span[0]);
                    Assert.True(kernel.TerminateProcess(foreignOwner).IsSuccess);return;
                }
            }
            Assert.True(host.HasPendingResult);
            Assert.True(host.AdvancePendingResult().IsSuccess);
            var result=await reply;
            Assert.Equal(VirtualDomainState.Created,kernel.QueryVirtualDomain(owner,result.Authority.Domain).Value);
            if(mode=="result-route-service-exit")
            {
                var exited=kernel.TerminateProcess(service);Assert.True(exited.IsSuccess,exited.Message+" Drain="+host.Drain().Message+" Calls="+global::System.Text.Json.JsonSerializer.Serialize(spy.Calls));
                Assert.Equal(0,spy.Calls.GetValueOrDefault("CloseOriginalChild"));
                Assert.Equal(VirtualDomainState.Created,kernel.QueryVirtualDomain(owner,result.Authority.Domain).Value);
                Assert.True(kernel.ConfigureVirtualDomain(owner,result.Authority.Domain,result.Authority.ConfigureCapability).IsSuccess);
                Assert.Equal(VirtualDomainState.Configured,kernel.QueryVirtualDomain(owner,result.Authority.Domain).Value);
                Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                Assert.Equal(0x48,buffer.Span[0]);Assert.Equal(0x71,foreign.Span[0]);
            }
        }
        else
        {
            Assert.False(host.ProcessNext().IsSuccess);
            await Assert.ThrowsAnyAsync<Exception>(async()=>await reply);
        }
        if(mode.StartsWith("failed-create-",StringComparison.Ordinal))
        {
            Assert.True(host.HasPendingResult);Assert.Equal(1,spy.Calls.GetValueOrDefault("CreateOriginalNestedChild"));
            var handedOff=mode.Contains("handoff",StringComparison.Ordinal);
            if(handedOff)Assert.True(host.AdvancePendingResult().IsSuccess);
            Assert.False(kernel.TerminateProcess(service).IsSuccess);
            Assert.Equal(0,spy.Calls.GetValueOrDefault("CloseOriginalChild"));
            Assert.Equal(!handedOff,host.HasPendingResult);Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
            Assert.Equal(0x48,buffer.Span[0]);Assert.Equal(0x71,foreign.Span[0]);
            spy.Fault="exact";var serviceExit=kernel.TerminateProcess(service);Assert.True(serviceExit.IsSuccess,serviceExit.Message);
            Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalChild"));
            Assert.False(host.HasPendingResult);Assert.True(host.Drain().IsSuccess);Assert.Equal(1,spy.Calls.GetValueOrDefault("CreateOriginalNestedChild"));
            Assert.Equal(0x48,buffer.Span[0]);Assert.Equal(0x71,foreign.Span[0]);
            Assert.True(kernel.QueryVirtualDomain(owner,parent.Domain).IsSuccess);
            var ownerExit=kernel.TerminateProcess(owner);Assert.True(ownerExit.IsSuccess,ownerExit.Message);
            Assert.Equal(2,spy.Calls.GetValueOrDefault("CloseOriginalChild"));
            Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
            Assert.Equal(0,parentRuntime.ActiveBindingCount);Assert.Equal(0x71,foreign.Span[0]);
            Assert.True(kernel.TerminateProcess(foreignOwner).IsSuccess);return;
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
        if(routeResult)
        {
            Assert.False((bool)invocation.GetType().GetProperty("VirtualCreateNeverInvoked")!.GetValue(invocation)!);
            Assert.False((bool)invocation.GetType().GetProperty("VirtualCreateNoEffect")!.GetValue(invocation)!);
            Assert.False((bool)invocation.GetType().GetProperty("VirtualCreateContained")!.GetValue(invocation)!);
        }
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
                Assert.Equal(0,spy.Calls.GetValueOrDefault("CloseOriginalChild"));
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
        Assert.True(kernel.Processes.Resolve(foreignOwner).IsSuccess);
        if(mode=="result-route-service-exit")Assert.True(kernel.TerminateProcess(foreignOwner).IsSuccess);
    }
}
