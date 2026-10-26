using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform.Host;
using SingPlus.Platform.HybridCpu;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;
using SingPlus.Runtime;
using SingPlus.Sip.Virtualization;
using SingPlus.Tests.Platform;
using SingPlus.Sip;

namespace SingPlus.Tests.Virtualization;

public sealed class UnpublishedRootCreationTests
{
    [Theory]
    [InlineData("exact")] [InlineData("lost-close")] [InlineData("copy-recovery")] [InlineData("loss-recovery")]
    [InlineData("lost-root")]
    [InlineData("lost-root-copy")] [InlineData("lost-root-loss")]
    public async Task FailedFirstChildFactoryServiceExitRetainsUnknownThenClosesOriginalRoot(string mode)
    {
        ExactArtifactReleaseConsumerTests.IReleaseRuntime raw =DispatchProxy.Create<SingPlus.Tests.Platform.ExactArtifactReleaseConsumerTests.IReleaseRuntime,SingPlus.Tests.Platform.ExactArtifactReleaseConsumerTests.ReleaseSpy>();
        ExactArtifactReleaseConsumerTests.ReleaseSpy spy =(SingPlus.Tests.Platform.ExactArtifactReleaseConsumerTests.ReleaseSpy)(object)raw;
        spy.Inner=new();spy.Fault="child-create-query-throw";
        NeutralDomainRuntimeFacade runtime =new NeutralDomainRuntimeFacade();
        RuntimeKernel kernel =new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime,new HybridCpuExecutableChildAdapter(spy.Inner,raw)));
        ProcessHandle caller =TestFixtures.Create(kernel,3011,30110).Handle; ProcessHandle service =TestFixtures.Create(kernel,3012,30120).Handle;
        ProcessHandle foreignOwner =TestFixtures.Create(kernel,3013,30130).Handle;
        OwnedBuffer<byte> region =kernel.AllocateBuffer<byte>(caller,64).Value!;region.Span[0]=0x48;
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(foreignOwner,16).Value!;foreign.Span[0]=0x71;
        CapabilityId cap =kernel.MintCapability(new(30110),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"failed-first-child",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(caller,descriptor,[]).Value;
        RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        ValueTask<VirtualDomainAuthorityResponse> reply =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session)).CreateAsync(new(cap,new(1,4096)));
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await reply);
        Assert.False(kernel.TerminateProcess(service).IsSuccess);Assert.Equal(1,spy.CreateCalls);
        Assert.Equal(0,spy.ChildCloseCalls);Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        if(mode!="exact")
        {
            spy.Fault="child-close-lost-reply";Assert.False(kernel.TerminateProcess(service).IsSuccess);
            Assert.Equal(1,spy.ChildCloseCalls);Assert.Equal(1,runtime.ActiveBindingCount);
            Assert.Equal(0x48,region.Span[0]);Assert.Equal(0x71,foreign.Span[0]);spy.Fault="exact";
            if(mode is "copy-recovery" or "loss-recovery")
            {
                const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
                IDictionary rows =(IDictionary)kernel.PlatformAuthority.GetType().GetField("_originalChildOwners",flags)!.GetValue(kernel.PlatformAuthority)!;
                object child =Assert.Single(rows.Values.Cast<object>());
                PropertyInfo property =child.GetType().GetProperty("OriginalUnreturnedCreation")!;
                object original =property.GetValue(child)!;Assert.NotNull(original);
                property.SetValue(child,mode=="loss-recovery"?null:typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null));
                Assert.False(kernel.TerminateProcess(service).IsSuccess);Assert.Equal(1,spy.ChildCloseCalls);
                Assert.Equal(0x48,region.Span[0]);Assert.Equal(0x71,foreign.Span[0]);property.SetValue(child,original);
            }
        }
        if(mode.StartsWith("lost-root",StringComparison.Ordinal))
        {
            spy.Fault="parent-lost-reply";spy.AfterParentQuery=()=>throw new IOException("Root Close query lost after child closure");
            Assert.False(kernel.TerminateProcess(service).IsSuccess);
            Assert.Equal(1,spy.ChildCloseCalls);Assert.Equal(1,spy.ParentCloseCalls);
            Assert.Equal(0x48,region.Span[0]);Assert.Equal(0x71,foreign.Span[0]);spy.AfterParentQuery=null;
            if(mode!="lost-root")
            {
                const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
                IDictionary rows =(IDictionary)kernel.PlatformAuthority.GetType().GetField("_originalChildOwners",flags)!.GetValue(kernel.PlatformAuthority)!;
                object child =Assert.Single(rows.Values.Cast<object>());
                PropertyInfo property =child.GetType().GetProperty("LastClosureReceipt")!;
                object original =property.GetValue(child)!;Assert.NotNull(original);
                property.SetValue(child,mode.EndsWith("loss",StringComparison.Ordinal)?null:typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null));
                Assert.False(kernel.TerminateProcess(service).IsSuccess);Assert.Equal(1,spy.ParentCloseCalls);
                Assert.Equal(0x48,region.Span[0]);Assert.Equal(0x71,foreign.Span[0]);property.SetValue(child,original);
            }
        }
        spy.Fault="exact"; KernelResult exit =kernel.TerminateProcess(service);Assert.True(exit.IsSuccess,exit.Message);
        if(mode.StartsWith("lost-root",StringComparison.Ordinal))Assert.Equal(1,spy.ParentCloseCalls);
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
    [InlineData(false,"host-flag")] [InlineData(true,"host-flag")]
    [InlineData(false,"completion-flags")] [InlineData(true,"completion-flags")]
    [InlineData(false,"cleanup-copy")] [InlineData(true,"cleanup-copy")]
    [InlineData(false,"cleanup-loss")] [InlineData(true,"cleanup-loss")]
    [InlineData(false,"closure-copy")] [InlineData(true,"closure-copy")]
    [InlineData(false,"closure-loss")] [InlineData(true,"closure-loss")]
    [InlineData(true,"publication-late")]
    public async Task ServiceExitClosesOnlyUndeliveredOriginalCreation(bool native,string mode)
    {
        HostPlatformAuthorityProvider provider =new HostPlatformAuthorityProvider();
        NeutralDomainRuntimeFacade runtime =new NeutralDomainRuntimeFacade();
        ExactNestedDomainConsumerTests.INestedRuntime raw =DispatchProxy.Create<SingPlus.Tests.Platform.ExactNestedDomainConsumerTests.INestedRuntime,SingPlus.Tests.Platform.ExactNestedDomainConsumerTests.NestedSpy>();
        ExactNestedDomainConsumerTests.NestedSpy spy =(SingPlus.Tests.Platform.ExactNestedDomainConsumerTests.NestedSpy)(object)raw;
        RuntimeKernel kernel =new RuntimeKernel(native?new HybridCpuPlatformAuthorityProvider(runtime,new HybridCpuExecutableChildAdapter(spy.Inner,raw)):provider);
        int Creates()=>native?spy.Calls.GetValueOrDefault("CreateOriginalChild"):provider.CreateVirtualDomainCallCount;
        int Closes()=>native?spy.Calls.GetValueOrDefault("CloseOriginalChild"):provider.RevokeVirtualDomainCallCount;
        ProcessHandle caller =TestFixtures.Create(kernel,2991,29910).Handle;
        ProcessHandle service =TestFixtures.Create(kernel,2992,29920).Handle;
        ProcessHandle foreignOwner =TestFixtures.Create(kernel,2993,29930).Handle;
        CapabilityId administration =kernel.MintCapability(new(29930),foreignOwner,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(foreignOwner,administration,caller,"root-create",[new(ServiceBudgetDimension.OwnedMemoryBytes,32)]).Value!.ProcessBudget;
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        OwnedBuffer<byte> buffer =kernel.AllocateBuffer<byte>(caller,32).Value!;buffer.Span.Fill(0x48);
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(foreignOwner,16).Value!;foreign.Span.Fill(0x71);
        CapabilityId cap =kernel.MintCapability(new(29910),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        ProcessHandle liveOwner =native?foreignOwner:caller;
        CapabilityId liveCap =native?kernel.MintCapability(new(29930),foreignOwner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId:cap;
        VirtualDomainAuthoritySet live =kernel.CreateVirtualDomain(liveOwner,liveCap,new(1,4096)).Value!;
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"unpublished-root",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(caller,descriptor,[]).Value;
        RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        ValueTask<VirtualDomainAuthorityResponse> reply =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session)).CreateAsync(new(cap,new(1,4096)));
        kernel.SessionInvocationSettlementReservedHook=()=>{kernel.SessionInvocationSettlementReservedHook=null;throw new IOException("Reply action never started");};
        Assert.Throws<IOException>(()=>host.ProcessNext());Assert.True(host.HasPendingResult);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        object pending =host.GetType().GetField("_pendingResult",flags)!.GetValue(host)!;
        VirtualDomainAuthoritySet child =Assert.IsType<VirtualDomainAuthorityResponse>(pending.GetType().GetField("Payload",flags)!.GetValue(pending)).Authority;
        Assert.Equal(2,Creates());Assert.Equal(0,Closes());
        if(mode is "host-flag" or "completion-flags")
        {
            pending.GetType().GetField("CreationClosed",flags)!.SetValue(pending,true);
            if(mode=="completion-flags")
            {
                object registry =typeof(RuntimeKernel).GetProperty("SessionInvocations",flags)!.GetValue(kernel)!;
                IDictionary rows =(IDictionary)registry.GetType().GetField("_ownedRecords",flags)!.GetValue(registry)!;
                object record =Assert.Single(rows.Values.Cast<object>());
                foreach(string flag in new[]{"VirtualCreateContained","VirtualCreateNeverInvoked","VirtualCreateNoEffect","VirtualCreateCleanupCompleted"})
                    record.GetType().GetProperty(flag,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.SetValue(record,true);
            }
        }
        if(mode is "copy" or "missing" or "foreign")
        {
            object invocations =typeof(RuntimeKernel).GetProperty("SessionInvocations",flags)!.GetValue(kernel)!;
            IDictionary records =(IDictionary)invocations.GetType().GetField("_ownedRecords",flags)!.GetValue(invocations)!;
            object invocation =Assert.Single(records.Values.Cast<object>());
            PropertyInfo property =invocation.GetType().GetProperty("OriginalVirtualCreate")!; object original =property.GetValue(invocation)!;
            object? replacement=mode=="copy"?typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null):null;
            if(mode=="foreign")
            {
                IDictionary rows =(IDictionary)kernel.PlatformAuthority.GetType().GetField(native?"_domains":"_originalVirtualDomainOwners",flags)!.GetValue(kernel.PlatformAuthority)!;
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
            Assert.Equal(1,Closes());Assert.True(host.HasPendingResult);
            pending.GetType().GetField("CreationCleanupStarted",flags)!.SetValue(pending,false);
            Assert.False(host.AdvancePendingResult().IsSuccess);
            FieldInfo cleanup =pending.GetType().GetField("CleanupRequest",flags)!;
            object originalCleanup =cleanup.GetValue(pending)!;Assert.NotNull(originalCleanup);
            cleanup.SetValue(pending,null);
            Assert.False(host.AdvancePendingResult().IsSuccess);Assert.False(kernel.TerminateProcess(service).IsSuccess);
            Assert.Equal(1,Closes());cleanup.SetValue(pending,originalCleanup);
            Assert.Equal(32UL,Used());Assert.Equal(0x48,buffer.Span[0]);Assert.Equal(0x71,foreign.Span[0]);
            spy.Fault="exact";
        }
        if(mode is "cleanup-copy" or "cleanup-loss" or "closure-copy" or "closure-loss")
        {
            object registry =typeof(RuntimeKernel).GetProperty("SessionInvocations",flags)!.GetValue(kernel)!;
            IDictionary rows =(IDictionary)registry.GetType().GetField("_ownedRecords",flags)!.GetValue(registry)!;
            object record =Assert.Single(rows.Values.Cast<object>());
            PropertyInfo property =record.GetType().GetProperty(mode.StartsWith("cleanup")?"VirtualCreateCleanupOutcome":"VirtualCreateOutcome")!;
            // Progress the same actual cleanup producer before host consumption,
            // then lose/copy only its retained fact. No new effect is requested.
            var publication =(EndpointSessionInvocationRegistry.OriginalPublication)pending.GetType().GetField("Publication",flags)!.GetValue(pending)!;
            Assert.True(kernel.CloseUnpublishedSessionVirtualCreation(caller,publication).IsSuccess);
            Assert.True(kernel.CompleteSessionVirtualCreationCleanup(publication).IsSuccess);
            object original=property.GetValue(record)!;Assert.NotNull(original);
            property.SetValue(record,mode.EndsWith("loss")?null:typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null));
            Assert.False(kernel.TerminateProcess(service).IsSuccess);Assert.NotNull(original);
            Assert.False(kernel.TerminateProcess(service).IsSuccess);Assert.True(host.HasPendingResult);
            Assert.Equal(1,Closes());Assert.Equal(32UL,Used());Assert.Equal(0x48,buffer.Span[0]);Assert.Equal(0x71,foreign.Span[0]);
            Assert.True(kernel.QueryVirtualDomain(liveOwner,live.Domain).IsSuccess);
            property.SetValue(record,original);
        }
        if(mode=="publication-late")
        {
            FieldInfo publication =pending.GetType().GetField("Publication",flags)!;
            object original =publication.GetValue(pending)!;
            spy.After=name=>
            {
                if(name!="CloseOriginalChild")return;
                spy.After=null;Assert.False(host.AdvancePendingResult().IsSuccess);
                publication.SetValue(pending,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null));
            };
            Assert.False(kernel.TerminateProcess(service).IsSuccess);Assert.True(host.HasPendingResult);
            Assert.Equal(1,Closes());Assert.Equal(32UL,Used());Assert.Equal(0x48,buffer.Span[0]);Assert.Equal(0x71,foreign.Span[0]);
            publication.SetValue(pending,original);
        }
        KernelResult exited =kernel.TerminateProcess(service);Assert.True(exited.IsSuccess,exited.Message);
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
