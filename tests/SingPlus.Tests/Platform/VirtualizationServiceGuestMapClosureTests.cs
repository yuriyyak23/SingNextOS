using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip.Virtualization;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Tests.Platform;

public sealed class VirtualizationServiceGuestMapClosureTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void NativeMapCapacityLookupRequiresOriginalParentHistoryAndKnownDecision(int fault)
    {
        var native=new NeutralDomainRuntimeFacade();
        var rootRequest=new NeutralOriginalDomainBindRequest(Guid.NewGuid(),((INeutralOriginalMappingClosureProvider)native).MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        var root=native.Bind(rootRequest).Observation.Lease;
        var peerRequest=new NeutralOriginalOwnedRegionMapRequest(Guid.NewGuid(),rootRequest.RuntimeIncarnation,rootRequest.InvocationId,root,new(128,64,NeutralMemoryAccess.Read));
        Assert.True(native.MapOwnedRegion(peerRequest).Observation.IsMapped);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        typeof(NeutralDomainRuntimeFacade).GetField("_nextResource",flags)!.SetValue(native,ulong.MaxValue);
        var request=peerRequest with {InvocationId=Guid.NewGuid(),Slice=new(0,64,NeutralMemoryAccess.Read)};
        var raw=native.MapOwnedRegion(request);Assert.True(raw.ProvenNoEffect);Assert.False(raw.Observation.IsMapped);
        Assert.Equal(raw,native.ResolveOriginalOwnedRegionMap(request));
        Assert.Null(native.ResolveOriginalOwnedRegionMap(request with {DomainBindInvocationId=Guid.NewGuid()}));
        Assert.False(native.MapOwnedRegion(request).ProvenNoEffect);
        var dependencies=typeof(NeutralDomainRuntimeFacade).GetField("_dependencies",flags)!.GetValue(native)!;
        var domains=(IDictionary)dependencies.GetType().GetField("Domains",flags)!.GetValue(dependencies)!;
        var parent=Assert.Single(domains.Values.Cast<object>());
        var facts=(IList)parent.GetType().GetProperty("OriginalMaps")!.GetValue(parent)!;
        var original=facts[1]!;
        if(fault==0)facts[1]=typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null);
        else if(fault==1)facts.RemoveAt(1);
        else original.GetType().GetProperty("Raw")!.SetValue(original,raw with {Observation=new(){Decision=(NeutralOwnedRegionMapDecision)999}});
        Assert.Null(native.ResolveOriginalOwnedRegionMap(request));
        Assert.Equal(1,native.ActiveOwnedRegionMappingCount);Assert.Equal(1,native.ActiveBindingCount);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ParentCapacityNoEffectClosesOnlyOriginalFailedStageBeforeCallerReclaim(bool lost)
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var native=new NeutralDomainRuntimeFacade();var proxy=DispatchProxy.Create<INativeMapLossRuntime,NativeMapLossSpy>();
        var spy=(NativeMapLossSpy)(object)proxy;spy.Inner=native;spy.Lose=false;
        var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(proxy,new HybridCpuExecutableChildAdapter()));
        var (_,owner)=TestFixtures.Create(kernel,2971,29710);var service=TestFixtures.Create(kernel,2972,29720).Handle;
        var buffer=kernel.AllocateBuffer<byte>(owner,64).Value!;var peer=kernel.AllocateBuffer<byte>(owner,64).Value!;peer.Span[0]=0x6B;
        var foreign=kernel.AllocateBuffer<byte>(service,32).Value!;
        var cap=kernel.MintCapability(new(29710),owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var domain=kernel.CreateVirtualDomain(owner,cap,new(1,4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess);
        CapabilityId Memory(RegionHandle region)=>kernel.MintCapability(new(29710),owner,ResourceKind.MemoryRegion,CapabilityResourceIds.MemoryRegion(region.RegionId),CapabilityRights.Map|CapabilityRights.Read).Value!.CapabilityId;
        var independent=kernel.MapGuestRegion(owner,domain.Domain,domain.MemoryCapability,Memory(peer.Handle),peer.Handle,new(128,64),GuestMemoryAccess.Read);
        Assert.True(independent.IsSuccess,independent.Message);
        typeof(NeutralDomainRuntimeFacade).GetField("_nextResource",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(native,ulong.MaxValue);
        spy.Lose=lost;
        var protocol=VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"parent-capacity",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(owner,descriptor,[]).Value;
        var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        var client=VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        var pending=client.MapGuestRegionAsync(new(domain.Domain,domain.MemoryCapability,Memory(buffer.Handle),buffer.Handle,new(0,64),GuestMemoryAccess.Read));
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await pending);
        Assert.True(host.HasPendingResult);
        var custodyField=host.GetType().GetField("_pendingOperation",flags)!;
        var custody=custodyField.GetValue(host)!;
        custodyField.SetValue(host,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(custody,null));
        Assert.Equal(KernelError.PlatformFaulted,host.Drain().Error);
        custodyField.SetValue(host,custody);
        var lifetimeField=custody.GetType().GetField("Lifetime",flags)!;
        var lifetime=lifetimeField.GetValue(custody)!;
        lifetimeField.SetValue(custody,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(lifetime,null));
        Assert.Equal(KernelError.WrongSessionOwner,host.Drain().Error);
        lifetimeField.SetValue(custody,lifetime);
        var invocationRegistry=(EndpointSessionInvocationRegistry)kernel.GetType().GetField("_endpointSessionInvocationRegistry",flags)!.GetValue(kernel)!;
        var routes=(IDictionary)invocationRegistry.GetType().GetField("_records",flags)!.GetValue(invocationRegistry)!;
        var key=Assert.Single(routes.Keys.Cast<object>());
        var originalRecord=routes[key]!;
        routes[key]=typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(originalRecord,null);
        Assert.Equal(KernelError.WrongSessionOwner,host.Drain().Error);
        routes[key]=originalRecord;
        var outcomeProperty=originalRecord.GetType().GetProperty("SettlementOutcome")!;
        var outcome=(ResponseEnvelope)outcomeProperty.GetValue(originalRecord)!;
        outcomeProperty.SetValue(originalRecord,outcome with {});
        Assert.False(host.Drain().IsSuccess); // Equal tuple is not original cancellation.
        outcomeProperty.SetValue(originalRecord,outcome);
        Assert.True(host.AdvancePendingResult().IsSuccess); // Custody handoff only.
        Assert.False(host.HasPendingResult);
        Assert.Same(((ChannelEnvelope)custody.GetType().GetField("Request",flags)!.GetValue(custody)!),
            originalRecord.GetType().GetProperty("OriginalAcceptedRequest")!.GetValue(originalRecord));
        Assert.False(host.ProcessNext().IsSuccess); // Draining; no effect replay.
        Assert.Equal(2,spy.Maps);Assert.Equal(1,native.ActiveOwnedRegionMappingCount);Assert.Equal(0x6B,peer.Span[0]);
        Assert.False(kernel.AcquireRegionUse(owner,peer.Handle,RegionUseMode.ExclusiveRead,new(0,64)).IsSuccess);
        if(lost)
        {
            Assert.False(kernel.AcquireRegionUse(owner,buffer.Handle,RegionUseMode.ExclusiveRead,new(0,64)).IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);spy.Lose=false;
        }
        var closed=kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
        Assert.False(host.HasPendingResult);
        Assert.Equal(2,spy.Maps);Assert.Equal(0,native.ActiveOwnedRegionMappingCount);Assert.Equal(0,native.ActiveBindingCount);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==peer.Handle).State);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==foreign.Handle).State);
    }
    public interface INativeMapLossRuntime : INeutralDomainRuntime,INeutralOriginalDomainCloseProvider,INeutralOriginalMapOutcomeProvider { }
    public class NativeMapLossSpy : DispatchProxy
    {
        public NeutralDomainRuntimeFacade Inner=null!;
        public bool Lose=true;
        public int Maps;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(Lose && method!.Name=="ResolveOriginalOwnedRegionMap")throw new IOException("Native Map query lost");
            var result=method!.Invoke(Inner,args);
            if(method.Name=="MapOwnedRegion"){Maps++;if(Lose)throw new IOException("Native Map reply lost after effect");}
            return result;
        }
    }
    public interface IParentLossProvider : OriginalRegionRevocationResolutionTests.IAdmissionProvider,
        IPlatformChildDomainProvider,IPlatformGuestMemoryProvider,IPlatformOriginalChildCreateProvider,
        IPlatformProviderIncarnationSource { }
    public class ParentLossSpy : DispatchProxy
    {
        public HybridCpuPlatformAuthorityProvider Inner=null!;
        public bool Lose=true;
        public int Maps;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(Lose && method!.Name=="ResolveOwnedRegionMap")throw new IOException("Original parent query lost");
            var result=method!.Invoke(Inner,args);
            if(method.Name=="MapOwnedRegionSlice")
            {Maps++;if(Lose)throw new IOException("Original parent reply lost after effect");}
            return result;
        }
    }
    public class MapLossSpy : ExactNestedGuestConsumerTests.GuestSpy
    {
        public bool Lose=true;
        public Action? AfterMap;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(Lose && method!.Name=="ResolveOriginalGuestMap")throw new IOException("Original Map query lost");
            var result=base.Invoke(method,args);
            if(method!.Name=="MapOriginalGuest")AfterMap?.Invoke();
            if(Lose && method!.Name=="MapOriginalGuest")throw new IOException("Original Map reply lost after effect");
            return result;
        }
    }

    [Theory]
    [InlineData(false,false,false,false)] [InlineData(true,false,false,false)] [InlineData(false,true,false,false)] [InlineData(false,false,true,false)]
    [InlineData(false,false,false,true)]
    public async Task OriginalServiceMapClosesInvocationAfterExactGuestParentAndCharge(bool copiedOwner,bool parentLoss,bool nativeLoss,bool closeRoute)
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var runtime=DispatchProxy.Create<ExactNestedArtifactConsumerTests.IOriginalNestedStartRuntime,MapLossSpy>();
        var spy=(MapLossSpy)(object)runtime;
        var native=new NeutralDomainRuntimeFacade();
        var nativeProxy=DispatchProxy.Create<INativeMapLossRuntime,NativeMapLossSpy>();
        var nativeSpy=(NativeMapLossSpy)(object)nativeProxy;nativeSpy.Inner=native;
        var provider=new HybridCpuPlatformAuthorityProvider(nativeLoss?nativeProxy:native,new HybridCpuExecutableChildAdapter(spy.Inner,runtime));
        var parentProxy=DispatchProxy.Create<IParentLossProvider,ParentLossSpy>();
        var parentSpy=(ParentLossSpy)(object)parentProxy;parentSpy.Inner=provider;
        spy.Lose=!parentLoss && !nativeLoss;
        var kernel=new RuntimeKernel(parentLoss?parentProxy:provider);
        var (process,owner)=TestFixtures.Create(kernel,2601,26010);
        var service=TestFixtures.Create(kernel,2602,26020).Handle;
        var admin=TestFixtures.Create(kernel,2603,26030).Handle;
        var foreign=kernel.AllocateBuffer<byte>(service,32).Value!;foreign.Span[0]=0x70;
        const ulong bytes=1024*1024;
        var ac=kernel.MintCapability(new(26030),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(admin,ac,owner,"Map service",
            [new(ServiceBudgetDimension.OwnedMemoryBytes,bytes*2),new(ServiceBudgetDimension.PinnedMappedMemoryBytes,bytes),new(ServiceBudgetDimension.GuestMemoryBytes,bytes)]).Value!.ProcessBudget;
        var buffer=kernel.AllocateBuffer<byte>(owner,(int)bytes).Value!;
        var create=kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var domain=kernel.CreateVirtualDomain(owner,create,new(1,(long)bytes)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess);
        var memory=kernel.MintCapability(process.DomainId,owner,ResourceKind.MemoryRegion,CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),CapabilityRights.Map|CapabilityRights.Read|CapabilityRights.Execute).Value!.CapabilityId;
        var protocol=VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"Map service",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(owner,descriptor,[]).Value;
        var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        var client=VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        if(closeRoute)spy.AfterMap=()=>
        {
            Assert.Equal(KernelError.InvalidTransition,host.ProcessNext().Error);
            var originalSession=kernel.EndpointSessions.ResolveForService(session,service).Value!;
            Assert.True(kernel.Channels.Close(originalSession.ServiceEndpoint).IsSuccess);
        };
        var pending=client.MapGuestRegionAsync(new(domain.Domain,domain.MemoryCapability,memory,buffer.Handle,new(0,(long)bytes),GuestMemoryAccess.Read|GuestMemoryAccess.Execute){MaximumPrivateExecutableDataBytes=bytes});
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await pending);
        Assert.True(host.HasPendingResult);
        Assert.True(host.AdvancePendingResult().IsSuccess); // Provider/map/charges remain pinned below.
        Assert.False(host.ProcessNext().IsSuccess); // No second request/effect.
        Assert.Equal(bytes*2,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.False(kernel.AcquireRegionUse(owner,buffer.Handle,RegionUseMode.ExclusiveRead,new(0,(long)bytes)).IsSuccess);
        var registry=(EndpointSessionInvocationRegistry)kernel.GetType().GetField("_endpointSessionInvocationRegistry",flags)!.GetValue(kernel)!;
        var records=(IDictionary)registry.GetType().GetField("_ownedRecords",flags)!.GetValue(registry)!;
        var record=Assert.Single(records.Values.Cast<object>());
        Assert.NotNull(record.GetType().GetProperty("OriginalResponse")!.GetValue(record));
        var property=record.GetType().GetProperty("OriginalVirtualArtifactOwner")!;
        var original=property.GetValue(record)!;
        var mapping=(GuestRegionMappingHandle)record.GetType().GetProperty("VirtualArtifactMapping")!.GetValue(record)!;
        Assert.False(registry.ConsumeVirtualArtifactMappingClosure(service,original,mapping).IsSuccess);
        Assert.False(registry.ConsumeVirtualArtifactMappingClosure(owner,new object(),mapping).IsSuccess);
        spy.Lose=false;
        if(parentLoss || nativeLoss)
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(bytes*2,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(bytes,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.GuestMemoryBytes).Used);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
            Assert.Equal(1,native.ActiveOwnedRegionMappingCount);Assert.Equal(1,nativeLoss?nativeSpy.Maps:parentSpy.Maps);
        }
        parentSpy.Lose=false;
        nativeSpy.Lose=false;
        if(copiedOwner)
        {
            property.SetValue(record,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null));
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False((bool)record.GetType().GetProperty("VirtualArtifactContained")!.GetValue(record)!);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
            property.SetValue(record,original);
        }
        var closed=kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
        Assert.False(host.HasPendingResult);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage,u=>Assert.Equal(0UL,u.Used));
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        if(parentLoss || nativeLoss){Assert.Equal(1,nativeLoss?nativeSpy.Maps:parentSpy.Maps);Assert.Equal(0,spy.Calls.GetValueOrDefault("MapOriginalGuest"));}
        else{Assert.Equal(1,spy.Calls["MapOriginalGuest"]);Assert.Equal(1,spy.Calls["UnmapOriginalGuest"]);}
        Assert.Equal(0,native.ActiveOwnedRegionMappingCount);Assert.Equal(0,native.ActiveBindingCount);
        Assert.Equal(0x70,foreign.Span[0]);Assert.True(kernel.Processes.Resolve(service).IsSuccess);
    }
}
