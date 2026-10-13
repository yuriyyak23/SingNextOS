using System.Reflection;
using System.Collections;
using SingPlus.Contracts;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Tests.Platform;
public sealed class NativeOriginalBindOutcomeTests
{
    public interface ILossRuntime : INeutralDomainRuntime,INeutralOriginalDomainCloseProvider,INeutralOriginalBindOutcomeProvider { }
    public class LossSpy : DispatchProxy
    {
        public NeutralDomainRuntimeFacade Inner=new();
        public bool Lose=true;
        public int Binds;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(Lose && method!.Name=="ResolveOriginalDomainBind")throw new IOException("Original Bind query lost");
            var result=method!.Invoke(Inner,args);
            if(method.Name=="Bind"){Binds++;if(Lose)throw new IOException("Original Bind reply lost after effect");}
            return result;
        }
    }
    [Fact]
    public void ActualKernelRootLossRetainsRegionChargeUntilSameOriginalCleanup()
    {
        var native=DispatchProxy.Create<ILossRuntime,LossSpy>();var spy=(LossSpy)(object)native;
        var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(native));
        var (_,owner)=TestFixtures.Create(kernel,2701,27010);var admin=TestFixtures.Create(kernel,2702,27020).Handle;
        var cap=kernel.MintCapability(new(27020),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(admin,cap,owner,"native Bind",[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]).Value!.ProcessBudget;
        var region=kernel.AllocateBuffer<byte>(owner,64).Value!;
        Assert.False(kernel.BindPlatformDomain(owner).IsSuccess);Assert.Equal(1,spy.Inner.ActiveBindingCount);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(1,spy.Binds);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        spy.Lose=false;var closed=kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(1,spy.Binds);Assert.Equal(0,spy.Inner.ActiveBindingCount);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage,u=>Assert.Equal(0UL,u.Used));
        Assert.True(kernel.Processes.Resolve(admin).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GeneratedCreateLossRequiresOriginalInvocationClosure(bool copiedOwner)
    {
        var native=DispatchProxy.Create<ILossRuntime,LossSpy>();var spy=(LossSpy)(object)native;
        var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(native,new YAKSys_Hybrid_CPU.ExecutableAdapter.HybridCpuExecutableChildAdapter()));
        var (process,owner)=TestFixtures.Create(kernel,2801,28010);var service=TestFixtures.Create(kernel,2802,28020).Handle;
        var region=kernel.AllocateBuffer<byte>(owner,64).Value!;
        var cap=kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var protocol=SingPlus.Sip.Virtualization.VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"Create",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,
            SingPlus.Sip.Virtualization.VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(owner,descriptor,[]).Value;
        var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        var client=SingPlus.Sip.Virtualization.VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        var pending=client.CreateAsync(new(cap,new(1,4096)));
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await pending);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(1,spy.Binds);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var registry=(EndpointSessionInvocationRegistry)kernel.GetType().GetField("_endpointSessionInvocationRegistry",flags)!.GetValue(kernel)!;
        var records=(IDictionary)registry.GetType().GetField("_ownedRecords",flags)!.GetValue(registry)!;
        var record=Assert.Single(records.Values.Cast<object>());
        var property=record.GetType().GetProperty("OriginalVirtualCreate")!;var original=property.GetValue(record)!;
        var invocation=(EndpointSessionInvocationHandle)record.GetType().GetProperty("Handle")!.GetValue(record)!;
        Assert.False(registry.ConsumeVirtualCreationClosure(invocation,service,original).IsSuccess);
        Assert.False(registry.ConsumeVirtualCreationClosure(invocation,owner,new object()).IsSuccess);
        Assert.False(registry.ConsumeVirtualCreationNeverInvoked(invocation,service).IsSuccess);
        Assert.False(registry.ConsumeVirtualCreationNeverInvoked(invocation,owner).IsSuccess);
        spy.Lose=false;
        if(copiedOwner)
        {
            property.SetValue(record,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null));
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False((bool)record.GetType().GetProperty("VirtualCreateContained")!.GetValue(record)!);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            property.SetValue(record,original);
        }
        var closed=kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(1,spy.Binds);Assert.Equal(0,spy.Inner.ActiveBindingCount);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        Assert.True(kernel.Processes.Resolve(service).IsSuccess);
    }
}
