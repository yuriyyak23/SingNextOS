using System.Collections;
using System.Reflection;
using HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip.Virtualization;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Tests.Platform;

public sealed class VirtualizationServiceArtifactClosureTests
{
    public class LossSpy : ExactNestedGuestConsumerTests.GuestSpy
    {
        public bool Lose=true,StartLoss;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(Lose && (StartLoss?method!.Name=="ResolveOriginalChildExecutionStart":
                method!.Name is "ResolveChildExecutableArtifactBind" or "ResolveOriginalArtifactBindDisposition"))throw new IOException("Original query lost");
            var result=base.Invoke(method,args);
            if(Lose && method!.Name==(StartLoss?"StartOriginalChildExecution":"BindChildExecutableArtifact"))throw new IOException("Original reply lost after effect");
            return result;
        }
    }

    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public async Task ActualServiceArtifactFailureClosesOnlySameOriginalAfterExactMappingCleanup(bool start,bool copiedOwner)
    {
        var runtime=DispatchProxy.Create<ExactNestedArtifactConsumerTests.IOriginalNestedStartRuntime,LossSpy>();
        var spy=(LossSpy)(object)runtime;spy.StartLoss=start;spy.Lose=!start;
        var adapter=new HybridCpuExecutableChildAdapter(spy.Inner,runtime);
        var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(),adapter));
        var (process,owner)=TestFixtures.Create(kernel,2401,24010);
        var service=TestFixtures.Create(kernel,2402,24020).Handle;
        var admin=TestFixtures.Create(kernel,2403,24030).Handle;
        const ulong bytes=1024*1024;
        var ac=kernel.MintCapability(new(24030),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(admin,ac,owner,"artifact-service",
            [new(ServiceBudgetDimension.OwnedMemoryBytes,bytes*2),new(ServiceBudgetDimension.PinnedMappedMemoryBytes,bytes),new(ServiceBudgetDimension.GuestMemoryBytes,bytes)]).Value!.ProcessBudget;
        var buffer=kernel.AllocateBuffer<byte>(owner,(int)bytes).Value!;
        var create=kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var domain=kernel.CreateVirtualDomain(owner,create,new(1,(long)bytes)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess);
        var memory=kernel.MintCapability(process.DomainId,owner,ResourceKind.MemoryRegion,CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),CapabilityRights.Map|CapabilityRights.Read|CapabilityRights.Execute).Value!.CapabilityId;
        var mapping=kernel.MapGuestRegion(owner,domain.Domain,domain.MemoryCapability,memory,buffer.Handle,new(0,(long)bytes),GuestMemoryAccess.Read|GuestMemoryAccess.Execute,bytes).Value!;
        var protocol=VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"artifact",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(owner,descriptor,[]).Value;
        var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        var client=VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        byte[] package=[];
        for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)
        {
            var path=Path.Combine(directory.FullName,"tests","SingPlus.Tests","Fixtures","OriginalExecutionTerminal","finite-branch-materialized.hcexe");
            if(File.Exists(path)){package=File.ReadAllBytes(path);break;}
        }
        Assert.True(package.Length>4096);
        var pending=client.BindExecutablePackageAsync(new(domain.Domain,domain.ExecuteCapability,mapping.Mapping,new(package),256));
        var dispatch=host.ProcessNext();
        if(start)
        {
            Assert.True(dispatch.IsSuccess,dispatch.Message);await pending;
            spy.Lose=true;pending=client.StartAsync(new(domain.Domain,domain.ExecuteCapability));dispatch=host.ProcessNext();
        }
        Assert.False(dispatch.IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await pending);
        Assert.Equal(bytes*2,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var registry=(EndpointSessionInvocationRegistry)kernel.GetType().GetField("_endpointSessionInvocationRegistry",flags)!.GetValue(kernel)!;
        var records=(IDictionary)registry.GetType().GetField("_ownedRecords",flags)!.GetValue(registry)!;
        var record=records.Values.Cast<object>().Single(r=>(bool)r.GetType().GetProperty("AcceptedResponseCancelled")!.GetValue(r)!);
        var ownerProperty=record.GetType().GetProperty("OriginalVirtualArtifactOwner")!;
        var original=ownerProperty.GetValue(record)!;
        var handle=(EndpointSessionInvocationHandle)record.GetType().GetProperty("Handle")!.GetValue(record)!;
        Assert.False(registry.BindVirtualArtifact(handle,service,original,mapping.Mapping).IsSuccess);
        Assert.False(registry.BindVirtualArtifact(handle,owner,new object(),mapping.Mapping).IsSuccess);
        Assert.False(registry.ConsumeVirtualArtifactMappingClosure(service,original,mapping.Mapping).IsSuccess);
        Assert.False(registry.ConsumeVirtualArtifactMappingClosure(owner,new object(),mapping.Mapping).IsSuccess);
        spy.Lose=false;
        if(copiedOwner)
        {
            ownerProperty.SetValue(record,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null));
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
            Assert.Equal(bytes,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.False((bool)record.GetType().GetProperty("VirtualArtifactContained")!.GetValue(record)!);
            ownerProperty.SetValue(record,original);
        }
        var closed=kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
        Assert.False(buffer.IsValid);Assert.All(kernel.QueryBudget(budget).Value!.Usage,u=>Assert.Equal(0UL,u.Used));
        Assert.Equal(1,spy.Calls["BindChildExecutableArtifact"]);Assert.Equal(start?1:0,spy.Calls.GetValueOrDefault("StartOriginalChildExecution"));
        Assert.Equal(1,spy.Calls["ReleaseChildExecutableArtifact"]);Assert.True(kernel.Processes.Resolve(service).IsSuccess);
    }

    [Fact]
    public void PackageCopyPreservesPrivateBytesAndExistingSmallLimit()
    {
        var bytes=new byte[11008];bytes[0]=42;var package=new BoundedExecutablePackage(bytes);
        bytes[0]=7;var copy=package.ToArray();Assert.Equal(42,copy[0]);copy[0]=9;Assert.Equal(42,package.ToArray()[0]);
        Assert.Throws<ArgumentOutOfRangeException>(()=>new BoundedExecutablePackage([]));
        Assert.Throws<ArgumentOutOfRangeException>(()=>new BoundedExecutablePackage(new byte[BoundedExecutablePackage.MaxLength+1]));
        Assert.Throws<ArgumentOutOfRangeException>(()=>new SingPlus.Sip.Native.BoundedBytes(new byte[4097]));
    }
}
