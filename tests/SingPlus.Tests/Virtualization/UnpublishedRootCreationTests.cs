using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform.Host;
using SingPlus.Platform.HybridCpu;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;
using SingPlus.Runtime;
using SingPlus.Sip.Virtualization;

namespace SingPlus.Tests.Virtualization;

public sealed class UnpublishedRootCreationTests
{
    [Fact]
    public async Task FailedFirstChildFactoryServiceExitRetainsUnknownThenClosesOriginalRoot()
    {
        var raw=DispatchProxy.Create<SingPlus.Tests.Platform.ExactArtifactReleaseConsumerTests.IReleaseRuntime,SingPlus.Tests.Platform.ExactArtifactReleaseConsumerTests.ReleaseSpy>();
        var spy=(SingPlus.Tests.Platform.ExactArtifactReleaseConsumerTests.ReleaseSpy)(object)raw;
        spy.Inner=new();spy.Fault="child-create-query-throw";
        var runtime=new NeutralDomainRuntimeFacade();
        var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime,new HybridCpuExecutableChildAdapter(spy.Inner,raw)));
        var caller=TestFixtures.Create(kernel,3011,30110).Handle;var service=TestFixtures.Create(kernel,3012,30120).Handle;
        var foreignOwner=TestFixtures.Create(kernel,3013,30130).Handle;
        var region=kernel.AllocateBuffer<byte>(caller,64).Value!;region.Span[0]=0x48;
        var foreign=kernel.AllocateBuffer<byte>(foreignOwner,16).Value!;foreign.Span[0]=0x71;
        var cap=kernel.MintCapability(new(30110),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var protocol=VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"failed-first-child",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(caller,descriptor,[]).Value;
        var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        var reply=VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session)).CreateAsync(new(cap,new(1,4096)));
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await reply);
        Assert.False(kernel.TerminateProcess(service).IsSuccess);Assert.Equal(1,spy.CreateCalls);
        Assert.Equal(0,spy.ChildCloseCalls);Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        spy.Fault="exact";var exit=kernel.TerminateProcess(service);Assert.True(exit.IsSuccess,exit.Message);
        Assert.Equal(1,spy.CreateCalls);Assert.Equal(1,spy.ChildCloseCalls);Assert.Equal(0,runtime.ActiveBindingCount);
        Assert.Equal(0x48,region.Span[0]);Assert.Equal(0x71,foreign.Span[0]);
        Assert.True(kernel.TerminateProcess(caller).IsSuccess);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        Assert.Equal(0x71,foreign.Span[0]);Assert.True(kernel.TerminateProcess(foreignOwner).IsSuccess);
    }

    [Theory]
    [InlineData(false,"exact")] [InlineData(true,"exact")]
    [InlineData(false,"copy")] [InlineData(false,"missing")] [InlineData(false,"foreign")]
    [InlineData(true,"copy")] [InlineData(true,"missing")] [InlineData(true,"foreign")] [InlineData(true,"close-loss")]
    public async Task ServiceExitClosesOnlyUndeliveredOriginalCreation(bool native,string mode)
    {
        var provider=new HostPlatformAuthorityProvider();
        var runtime=new NeutralDomainRuntimeFacade();
        var raw=DispatchProxy.Create<SingPlus.Tests.Platform.ExactNestedDomainConsumerTests.INestedRuntime,SingPlus.Tests.Platform.ExactNestedDomainConsumerTests.NestedSpy>();
        var spy=(SingPlus.Tests.Platform.ExactNestedDomainConsumerTests.NestedSpy)(object)raw;
        var kernel=new RuntimeKernel(native?new HybridCpuPlatformAuthorityProvider(runtime,new HybridCpuExecutableChildAdapter(spy.Inner,raw)):provider);
        int Creates()=>native?spy.Calls.GetValueOrDefault("CreateOriginalChild"):provider.CreateVirtualDomainCallCount;
        int Closes()=>native?spy.Calls.GetValueOrDefault("CloseOriginalChild"):provider.RevokeVirtualDomainCallCount;
        var caller=TestFixtures.Create(kernel,2991,29910).Handle;
        var service=TestFixtures.Create(kernel,2992,29920).Handle;
        var foreignOwner=TestFixtures.Create(kernel,2993,29930).Handle;
        var administration=kernel.MintCapability(new(29930),foreignOwner,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(foreignOwner,administration,caller,"root-create",[new(ServiceBudgetDimension.OwnedMemoryBytes,32)]).Value!.ProcessBudget;
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        var buffer=kernel.AllocateBuffer<byte>(caller,32).Value!;buffer.Span.Fill(0x48);
        var foreign=kernel.AllocateBuffer<byte>(foreignOwner,16).Value!;foreign.Span.Fill(0x71);
        var cap=kernel.MintCapability(new(29910),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var liveOwner=native?foreignOwner:caller;
        var liveCap=native?kernel.MintCapability(new(29930),foreignOwner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId:cap;
        var live=kernel.CreateVirtualDomain(liveOwner,liveCap,new(1,4096)).Value!;
        var protocol=VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"unpublished-root",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(caller,descriptor,[]).Value;
        var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        var reply=VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session)).CreateAsync(new(cap,new(1,4096)));
        kernel.SessionInvocationSettlementReservedHook=()=>{kernel.SessionInvocationSettlementReservedHook=null;throw new IOException("Reply action never started");};
        Assert.Throws<IOException>(()=>host.ProcessNext());Assert.True(host.HasPendingResult);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var pending=host.GetType().GetField("_pendingResult",flags)!.GetValue(host)!;
        var child=Assert.IsType<VirtualDomainAuthorityResponse>(pending.GetType().GetField("Payload",flags)!.GetValue(pending)).Authority;
        Assert.Equal(2,Creates());Assert.Equal(0,Closes());
        if(mode is "copy" or "missing" or "foreign")
        {
            var invocations=typeof(RuntimeKernel).GetProperty("SessionInvocations",flags)!.GetValue(kernel)!;
            var records=(IDictionary)invocations.GetType().GetField("_ownedRecords",flags)!.GetValue(invocations)!;
            var invocation=Assert.Single(records.Values.Cast<object>());
            var property=invocation.GetType().GetProperty("OriginalVirtualCreate")!;var original=property.GetValue(invocation)!;
            object? replacement=mode=="copy"?typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null):null;
            if(mode=="foreign")
            {
                var rows=(IDictionary)kernel.PlatformAuthority.GetType().GetField(native?"_domains":"_originalVirtualDomainOwners",flags)!.GetValue(kernel.PlatformAuthority)!;
                replacement=rows.Values.Cast<object>().Select(r=>r.GetType().GetProperty(native?"BindInvocation":"CreateInvocation")!.GetValue(r)).Single(f=>f is not null && !ReferenceEquals(f,original));
            }
            property.SetValue(invocation,replacement);
            Assert.False(kernel.TerminateProcess(service).IsSuccess);Assert.True(host.HasPendingResult);
            Assert.Equal(0,Closes());Assert.Equal(32UL,Used());Assert.Equal(0x48,buffer.Span[0]);Assert.Equal(0x71,foreign.Span[0]);
            Assert.True(kernel.QueryVirtualDomain(liveOwner,live.Domain).IsSuccess);
            property.SetValue(invocation,original);
        }
        if(mode=="close-loss")
        {
            spy.Fault="close-loss";Assert.False(kernel.TerminateProcess(service).IsSuccess);
            Assert.Equal(1,Closes());Assert.True(host.HasPendingResult);Assert.False(host.AdvancePendingResult().IsSuccess);
            Assert.Equal(32UL,Used());Assert.Equal(0x48,buffer.Span[0]);Assert.Equal(0x71,foreign.Span[0]);
            spy.Fault="exact";
        }
        var exited=kernel.TerminateProcess(service);Assert.True(exited.IsSuccess,exited.Message);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await reply);
        Assert.False(host.HasPendingResult);Assert.Equal(1,Closes());
        Assert.False(kernel.QueryVirtualDomain(caller,child.Domain).IsSuccess);
        Assert.True(kernel.ConfigureVirtualDomain(liveOwner,live.Domain,live.ConfigureCapability).IsSuccess);
        Assert.Equal(0x48,buffer.Span[0]);Assert.Equal(0x71,foreign.Span[0]);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        Assert.True(kernel.TerminateProcess(caller).IsSuccess);
        Assert.Equal(2,Creates());Assert.Equal(native?1:2,Closes());
        Assert.Equal(0UL,Used());Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        Assert.Equal(0x71,foreign.Span[0]);Assert.True(kernel.TerminateProcess(foreignOwner).IsSuccess);Assert.Equal(2,Closes());
    }
}
