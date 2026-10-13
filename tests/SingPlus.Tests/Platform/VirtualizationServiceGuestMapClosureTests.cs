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
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(Lose && method!.Name=="ResolveOriginalGuestMap")throw new IOException("Original Map query lost");
            var result=base.Invoke(method,args);
            if(Lose && method!.Name=="MapOriginalGuest")throw new IOException("Original Map reply lost after effect");
            return result;
        }
    }

    [Theory]
    [InlineData(false,false,false)] [InlineData(true,false,false)] [InlineData(false,true,false)] [InlineData(false,false,true)]
    public async Task OriginalServiceMapClosesInvocationAfterExactGuestParentAndCharge(bool copiedOwner,bool parentLoss,bool nativeLoss)
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
        var pending=client.MapGuestRegionAsync(new(domain.Domain,domain.MemoryCapability,memory,buffer.Handle,new(0,(long)bytes),GuestMemoryAccess.Read|GuestMemoryAccess.Execute){MaximumPrivateExecutableDataBytes=bytes});
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await pending);
        Assert.Equal(bytes*2,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.False(kernel.AcquireRegionUse(owner,buffer.Handle,RegionUseMode.ExclusiveRead,new(0,(long)bytes)).IsSuccess);
        var registry=(EndpointSessionInvocationRegistry)kernel.GetType().GetField("_endpointSessionInvocationRegistry",flags)!.GetValue(kernel)!;
        var records=(IDictionary)registry.GetType().GetField("_ownedRecords",flags)!.GetValue(registry)!;
        var record=Assert.Single(records.Values.Cast<object>());
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
        Assert.All(kernel.QueryBudget(budget).Value!.Usage,u=>Assert.Equal(0UL,u.Used));
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        if(parentLoss || nativeLoss){Assert.Equal(1,nativeLoss?nativeSpy.Maps:parentSpy.Maps);Assert.Equal(0,spy.Calls.GetValueOrDefault("MapOriginalGuest"));}
        else{Assert.Equal(1,spy.Calls["MapOriginalGuest"]);Assert.Equal(1,spy.Calls["UnmapOriginalGuest"]);}
        Assert.Equal(0,native.ActiveOwnedRegionMappingCount);Assert.Equal(0,native.ActiveBindingCount);
        Assert.Equal(0x70,foreign.Span[0]);Assert.True(kernel.Processes.Resolve(service).IsSuccess);
    }
}
