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

public sealed class VirtualizationServiceIoBindClosureTests
{
    public class BindLossSpy : ExactVirtualIoBackingConsumerTests.BackingSpy
    {
        public bool LoseBind=true;
        public bool LoseContainment;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(LoseBind && method!.Name=="ResolveOriginalVirtualIo")throw new IOException("Bind query lost");
            if(LoseContainment && method!.Name=="ResolveOriginalVirtualIoBackingContainment")throw new IOException("Containment query lost");
            var result=base.Invoke(method,args);
            if(LoseBind && method!.Name=="BindOriginalVirtualIo")throw new IOException("Bind reply lost after backing effect");
            if(LoseContainment && method!.Name=="ContainOriginalVirtualIoBacking")throw new IOException("Containment reply lost after effect");
            return result;
        }
    }

    [Theory]
    [InlineData(false,false,false)] [InlineData(true,false,false)] [InlineData(false,true,false)] [InlineData(true,true,false)]
    [InlineData(false,false,true)] [InlineData(true,false,true)] [InlineData(false,true,true)] [InlineData(true,true,true)]
    public async Task OriginalServiceBindClosesInvocationOnlyAfterBackingAndCharge(bool copyOwner,bool partialClose,bool cancellationThrow)
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var runtime=DispatchProxy.Create<ExactVirtualIoBackingConsumerTests.IOriginalBackingRuntime,BindLossSpy>();
        var spy=(BindLossSpy)(object)runtime;
        spy.Inner=new HybridCPU.ExternalRuntime.HybridCpuExternalRuntime();
        var adapter=new HybridCpuExecutableChildAdapter(spy.Inner,runtime);
        var native=new NeutralDomainRuntimeFacade();
        var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(native,adapter));
        var (process,owner)=TestFixtures.Create(kernel,2501,25010);
        var service=TestFixtures.Create(kernel,2502,25020).Handle;
        var admin=TestFixtures.Create(kernel,2503,25030).Handle;
        var foreign=kernel.AllocateBuffer<byte>(service,32).Value!;foreign.Span[0]=0x6A;
        var ac=kernel.MintCapability(new(25030),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(admin,ac,owner,"IO Bind",[new(ServiceBudgetDimension.OwnedMemoryBytes,512)]).Value!.ProcessBudget;
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        var create=kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var domain=kernel.CreateVirtualDomain(owner,create,new(1,4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess);
        var root=kernel.QueryVirtualDomainPlatformBinding(owner,domain.Domain).Value;
        var dc=kernel.MintCapability(process.DomainId,owner,ResourceKind.Device,"device/io-bind",CapabilityRights.Read|CapabilityRights.Write|CapabilityRights.Configure).Value!.CapabilityId;
        var device=kernel.BindPlatformDevice(owner,root,dc,PlatformDeviceRights.Read|PlatformDeviceRights.Write).Value;
        var protocol=VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"IO Bind",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(owner,descriptor,[]).Value;
        var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        var client=VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        var pending=client.BindIoAsync(new(domain.Domain,device.LeaseId.Value,device.Generation.Value,VirtualIoAccess.Read|VirtualIoAccess.Write,512));
        if(cancellationThrow)
        {
            kernel.SessionInvocationSettlementReservedHook=()=>
            {
                kernel.SessionInvocationSettlementReservedHook=null;
                Assert.Equal(KernelError.InvalidTransition,host.ProcessNext().Error);
                throw new IOException("Accepted unknown Bind cancellation never started.");
            };
            Assert.Throws<IOException>(()=>host.ProcessNext());
            Assert.True(host.HasPendingResult);
            var custody=host.GetType().GetField("_pendingOperation",flags)!.GetValue(host)!;
            var requestField=custody.GetType().GetField("Request",flags)!;
            var originalRequest=(ChannelEnvelope)requestField.GetValue(custody)!;
            requestField.SetValue(custody,originalRequest with { });
            Assert.False(host.AdvancePendingResult().IsSuccess); // Equal request cannot authorize response retry.
            requestField.SetValue(custody,originalRequest);
            var lifetimeField=custody.GetType().GetField("Lifetime",flags)!;
            var lifetime=lifetimeField.GetValue(custody)!;
            lifetimeField.SetValue(custody,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(lifetime,null));
            Assert.False(host.AdvancePendingResult().IsSuccess);
            lifetimeField.SetValue(custody,lifetime);
            Assert.True(host.AdvancePendingResult().IsSuccess);
            Assert.False(host.HasPendingResult);
        }
        else Assert.False(host.ProcessNext().IsSuccess);
        await Assert.ThrowsAnyAsync<Exception>(async()=>await pending);
        Assert.Equal(512UL,Used());Assert.Equal(1,spy.Calls["BindOriginalVirtualIo"]);
        var registry=(EndpointSessionInvocationRegistry)kernel.GetType().GetField("_endpointSessionInvocationRegistry",flags)!.GetValue(kernel)!;
        var records=(IDictionary)registry.GetType().GetField("_ownedRecords",flags)!.GetValue(registry)!;
        var record=Assert.Single(records.Values.Cast<object>());
        var property=record.GetType().GetProperty("OriginalVirtualIoBind")!;
        var original=property.GetValue(record)!;
        var invocation=(EndpointSessionInvocationHandle)record.GetType().GetProperty("Handle")!.GetValue(record)!;
        Assert.False(registry.ConsumeVirtualIoAdmissionClosure(invocation,service,original).IsSuccess);
        Assert.False(registry.ConsumeVirtualIoAdmissionClosure(invocation,owner,new object()).IsSuccess);
        spy.LoseBind=false;
        if(partialClose)
        {
            spy.LoseContainment=true;
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(512UL,Used());Assert.Equal(1,native.ActiveDeviceLeaseCount);
            Assert.False((bool)record.GetType().GetProperty("VirtualIoBindContained")!.GetValue(record)!);
            spy.LoseContainment=false;
        }
        if(copyOwner)
        {
            property.SetValue(record,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null));
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False((bool)record.GetType().GetProperty("VirtualIoBindContained")!.GetValue(record)!);
            Assert.Equal(0UL,Used()); // Exact backing charge closed independently; invocation still pinned.
            property.SetValue(record,original);
        }
        var closed=kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Empty(records); // Exact canceled delivery and independent backing closure both finished.
        Assert.Equal(0UL,Used());Assert.Equal(0,native.ActiveDeviceLeaseCount);Assert.Equal(0,native.ActiveBindingCount);
        Assert.Equal(1,spy.Calls["BindOriginalVirtualIo"]);Assert.Equal(1,spy.Calls["ContainOriginalVirtualIoBacking"]);
        Assert.True(kernel.Processes.Resolve(service).IsSuccess);Assert.Equal(0x6A,foreign.Span[0]);
    }

    [Theory]
    [InlineData("deliver-first")] [InlineData("close-first")] [InlineData("charge-copy")]
    [InlineData("route-copy")]
    public void DeliveredBindRetiresOnlyAfterExactBackingClosureAndCharge(string mode)
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var runtime=DispatchProxy.Create<ExactVirtualIoBackingConsumerTests.IOriginalBackingRuntime,BindLossSpy>();
        var spy=(BindLossSpy)(object)runtime;spy.Inner=new();spy.LoseBind=false;
        var native=new NeutralDomainRuntimeFacade();
        var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(native,new HybridCpuExecutableChildAdapter(spy.Inner,runtime)));
        var (process,owner)=TestFixtures.Create(kernel,2511,25110);var service=TestFixtures.Create(kernel,2512,25120).Handle;
        var admin=TestFixtures.Create(kernel,2513,25130).Handle;
        var foreign=kernel.AllocateBuffer<byte>(service,32).Value!;foreign.Span[0]=0x6B;
        var ac=kernel.MintCapability(new(25130),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(admin,ac,owner,"delivered IO Bind",[new(ServiceBudgetDimension.OwnedMemoryBytes,1024)]).Value!.ProcessBudget;
        var region=kernel.AllocateBuffer<byte>(owner,512).Value!;
        var create=kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var domain=kernel.CreateVirtualDomain(owner,create,new(1,4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess);
        var root=kernel.QueryVirtualDomainPlatformBinding(owner,domain.Domain).Value;
        var dc=kernel.MintCapability(process.DomainId,owner,ResourceKind.Device,"device/delivered-bind",CapabilityRights.Read|CapabilityRights.Write|CapabilityRights.Configure).Value!.CapabilityId;
        var device=kernel.BindPlatformDevice(owner,root,dc,PlatformDeviceRights.Read|PlatformDeviceRights.Write).Value;
        var protocol=VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"delivered IO Bind",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(owner,descriptor,[]).Value;
        var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        var channel=kernel.ResolveSession(owner,session).Value!.Channel;
        // No response waiter: final publication alone must keep the invocation.
        Assert.True(kernel.Send(owner,service,channel,IVirtualizationServiceProtocol.Message_BindIoAsync,
            new BindVirtualIoRequest(domain.Domain,device.LeaseId.Value,device.Generation.Value,VirtualIoAccess.Read|VirtualIoAccess.Write,512)).IsSuccess);
        EndpointSessionInvocationRegistry.OriginalPublication? publication=null;
        kernel.SessionInvocationSettlementReservedHook=()=>
        {
            kernel.SessionInvocationSettlementReservedHook=null;
            Assert.Equal(KernelError.InvalidTransition,host.ProcessNext().Error);
            var pending=host.GetType().GetField("_pendingResult",flags)!.GetValue(host)!;
            publication=(EndpointSessionInvocationRegistry.OriginalPublication)pending.GetType().GetField("Publication",flags)!.GetValue(pending)!;
        };
        Assert.True(host.ProcessNext().IsSuccess);
        var registry=kernel.GetType().GetField("_endpointSessionInvocationRegistry",flags)!.GetValue(kernel)!;
        var records=(IDictionary)registry.GetType().GetField("_ownedRecords",flags)!.GetValue(registry)!;
        var routes=(IDictionary)registry.GetType().GetField("_records",flags)!.GetValue(registry)!;
        var record=Assert.Single(records.Values.Cast<object>());
        var local=Assert.Single(((IDictionary)kernel.GetType().GetField("_virtualIo",flags)!.GetValue(kernel)!).Values.Cast<object>());
        var binding=(VirtualIoBinding)local.GetType().GetProperty("Binding",flags)!.GetValue(local)!;
        Assert.Equal(1024UL,Used());
        if(mode!="close-first")
        {
            Assert.True(kernel.ReceiveResponse(owner,channel).IsSuccess);
            Assert.Single(records);Assert.Equal(1024UL,Used()); // Successful live Bind is still a backing user.
        }
        object? originalCharge=null;object? routeKey=null;
        kernel.BeforeVirtualIoClosureLocalCommit=()=>
        {
            kernel.BeforeVirtualIoClosureLocalCommit=null;
            if(mode=="charge-copy")
            {
                var field=local.GetType().GetProperty("BackingChargeOwner",flags)!;
                originalCharge=field.GetValue(local);field.SetValue(local,Clone(originalCharge!));
            }
            if(mode=="route-copy")
            {
                routeKey=Assert.Single(routes.Keys.Cast<object>());routes[routeKey]=Clone(record);
            }
        };
        Assert.Equal(mode is "deliver-first" or "close-first",kernel.CloseVirtualIo(binding).IsSuccess);
        if(mode is "charge-copy" or "route-copy")
        {
            Assert.Same(record,Assert.Single(records.Values.Cast<object>()));
            Assert.Equal(mode=="charge-copy" ? 1024UL : 512UL,Used());
            if(originalCharge is not null)local.GetType().GetProperty("BackingChargeOwner",flags)!.SetValue(local,originalCharge);
            if(routeKey is not null)routes[routeKey]=record;
            Assert.True(kernel.CloseVirtualIo(binding).IsSuccess);
        }
        if(mode=="close-first")
        {
            Assert.Single(records);Assert.Equal(512UL,Used()); // Exact closure is still not delivery.
            Assert.True(kernel.ReceiveResponse(owner,channel).IsSuccess);
        }
        Assert.Empty(records);Assert.Empty(routes);Assert.Equal(512UL,Used());
        Assert.True(kernel.ReadOriginalSessionPublication(publication!).IsSuccess);
        Assert.False(kernel.ReadOriginalSessionPublication((EndpointSessionInvocationRegistry.OriginalPublication)Clone(publication!)).IsSuccess);
        Assert.True(kernel.CloseVirtualIo(binding).IsSuccess);
        Assert.Equal(1,spy.Calls["BindOriginalVirtualIo"]);Assert.Equal(1,spy.Calls["CloseOriginalVirtualIo"]);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0UL,Used());
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        Assert.Equal(0x6B,foreign.Span[0]);Assert.True(kernel.Processes.Resolve(service).IsSuccess);
        static object Clone(object value)=>typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(value,null)!;
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
    }
}
