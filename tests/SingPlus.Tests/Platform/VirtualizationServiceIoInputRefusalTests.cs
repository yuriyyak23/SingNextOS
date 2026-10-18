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

public sealed class VirtualizationServiceIoInputRefusalTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;

    [Theory]
    [InlineData("bind","exact")] [InlineData("transfer","exact")] [InlineData("close","exact")]
    [InlineData("bind","cancel")] [InlineData("transfer","cancel")] [InlineData("close","cancel")]
    [InlineData("bind","delayed")] [InlineData("transfer","settlement-throw")]
    [InlineData("close","request-copy")] [InlineData("bind","route-copy")]
    [InlineData("transfer","delivery-copy")]
    [InlineData("create","exact")] [InlineData("create","cancel")] [InlineData("create","delayed")]
    [InlineData("create","settlement-throw")] [InlineData("create","request-copy")]
    [InlineData("create","route-copy")] [InlineData("create","delivery-copy")]
    public async Task OriginalPreAcceptanceRefusalRetiresOnlyDeliveredCustody(string kind,string mode)
    {
        var runtime=DispatchProxy.Create<ExactVirtualIoBackingConsumerTests.IOriginalBackingRuntime,VirtualizationServiceIoBindClosureTests.BindLossSpy>();
        var spy=(VirtualizationServiceIoBindClosureTests.BindLossSpy)(object)runtime;spy.Inner=new();spy.LoseBind=false;
        var native=new NeutralDomainRuntimeFacade();
        var provider=DispatchProxy.Create<VirtualizationServiceIoCloseClosureTests.IStageProvider,VirtualizationServiceIoCloseClosureTests.StageProvider>();
        var stage=(VirtualizationServiceIoCloseClosureTests.StageProvider)(object)provider;
        stage.Inner=new HybridCpuPlatformAuthorityProvider(native,new HybridCpuExecutableChildAdapter(spy.Inner,runtime));
        var kernel=new RuntimeKernel(provider);
        var (process,owner)=TestFixtures.Create(kernel,2521,25210);var service=TestFixtures.Create(kernel,2522,25220).Handle;
        var admin=TestFixtures.Create(kernel,2523,25230).Handle;
        var foreign=kernel.AllocateBuffer<byte>(service,32).Value!;foreign.Span[0]=0x6C;
        var ac=kernel.MintCapability(new(25230),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(admin,ac,owner,"IO input refusal",[new(ServiceBudgetDimension.OwnedMemoryBytes,1024)]).Value!.ProcessBudget;
        var region=kernel.AllocateBuffer<byte>(owner,512).Value!;
        var create=kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var domain=kernel.CreateVirtualDomain(owner,create,new(1,4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess);
        var root=kernel.QueryVirtualDomainPlatformBinding(owner,domain.Domain).Value;
        var dc=kernel.MintCapability(process.DomainId,owner,ResourceKind.Device,"device/input-refusal",CapabilityRights.Read|CapabilityRights.Write|CapabilityRights.Configure).Value!.CapabilityId;
        var device=kernel.BindPlatformDevice(owner,root,dc,PlatformDeviceRights.Read|PlatformDeviceRights.Write).Value;
        var io=kernel.BindVirtualIo(owner,domain.Domain,device,new(PlatformDeviceRights.Read|PlatformDeviceRights.Write,512)).Value;
        var protocol=VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"IO input refusal",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(owner,descriptor,[]).Value;
        var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        var channel=kernel.ResolveSession(owner,session).Value!.Channel;
        using var cancellation=new CancellationTokenSource();
        var client=VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session,cancellation.Token));
        var bind=new BindVirtualIoRequest(domain.Domain,device.LeaseId.Value,device.Generation.Value+(mode=="cancel" ? 0UL : 1UL),VirtualIoAccess.Read|VirtualIoAccess.Write,512);
        var generation=io.Generation+(mode=="cancel" ? 0UL : 1UL);
        Task? reply=null;
        if(mode=="delayed")Assert.True((kind=="create"
            ? kernel.Send(owner,service,channel,IVirtualizationServiceProtocol.Message_CreateAsync,new CreateVirtualDomainRequest(create,new(1,4096)))
            : kernel.Send(owner,service,channel,IVirtualizationServiceProtocol.Message_BindIoAsync,bind)).IsSuccess);
        else reply=kind switch
        {
            "create"=>client.CreateAsync(new(create,new(1,4096))).AsTask(),
            "bind"=>client.BindIoAsync(bind).AsTask(),
            "transfer"=>client.TransferIoAsync(new(io.Id,generation,0,false,2,default)).AsTask(),
            _=>client.CloseIoAsync(new(io.Id,generation)).AsTask()
        };
        if(mode=="cancel")cancellation.Cancel();
        object? pending=null;object? record=null;ChannelEnvelope? originalRequest=null;
        EndpointSessionInvocationRegistry.OriginalPublication? lifetime=null;
        kernel.SessionInvocationSettlementReservedHook=()=>
        {
            kernel.SessionInvocationSettlementReservedHook=null;
            Assert.Equal(KernelError.InvalidTransition,host.ProcessNext().Error);
            pending=host.GetType().GetField("_pendingOperation",Private)!.GetValue(host)!;
            originalRequest=(ChannelEnvelope)pending.GetType().GetField("Request",Private)!.GetValue(pending)!;
            lifetime=(EndpointSessionInvocationRegistry.OriginalPublication)pending.GetType().GetField("Lifetime",Private)!.GetValue(pending)!;
            var registry=(EndpointSessionInvocationRegistry)kernel.GetType().GetField("_endpointSessionInvocationRegistry",Private)!.GetValue(kernel)!;
            record=Assert.Single(((IDictionary)registry.GetType().GetField("_ownedRecords",Private)!.GetValue(registry)!).Values.Cast<object>());
            var invocation=(EndpointSessionInvocationHandle)record.GetType().GetProperty("Handle")!.GetValue(record)!;
            Assert.False(registry.BindOriginalVirtualizationInputRefusal(invocation,owner,originalRequest).IsSuccess);
            Assert.False(registry.BindOriginalVirtualizationInputRefusal(invocation,service,originalRequest with { }).IsSuccess);
            if(mode=="delivery-copy")record.GetType().GetProperty("OriginalDeliveredRequest")!.SetValue(record,originalRequest with { });
            if(mode is "settlement-throw" or "request-copy" or "route-copy")throw new IOException("Refusal response never started.");
        };
        if(mode is "settlement-throw" or "request-copy" or "route-copy")Assert.Throws<IOException>(()=>host.ProcessNext());
        else Assert.Equal(mode=="cancel",host.ProcessNext().IsSuccess);
        var invocations=(EndpointSessionInvocationRegistry)kernel.GetType().GetField("_endpointSessionInvocationRegistry",Private)!.GetValue(kernel)!;
        var records=(IDictionary)invocations.GetType().GetField("_ownedRecords",Private)!.GetValue(invocations)!;
        var routes=(IDictionary)invocations.GetType().GetField("_records",Private)!.GetValue(invocations)!;
        Assert.False((bool)record!.GetType().GetProperty("ServiceAccepted")!.GetValue(record)!);
        Assert.True((bool)record.GetType().GetProperty(kind switch {"create"=>"VirtualCreateNeverInvoked","bind"=>"VirtualIoBindNeverInvoked","transfer"=>"VirtualIoTransferNeverInvoked",_=>"VirtualIoCloseNeverInvoked"})!.GetValue(record)!);
        if(kind=="create")
        {
            Assert.Null(record.GetType().GetProperty("OriginalVirtualCreate")!.GetValue(record));
            Assert.Equal(root,kernel.QueryVirtualDomainPlatformBinding(owner,domain.Domain).Value);
            Assert.Equal(1,native.ActiveBindingCount); // Refused Create owns no part of this original root.
        }
        if(mode is "settlement-throw" or "request-copy" or "route-copy")
        {
            Assert.Single(records);Assert.True(host.HasPendingResult);
            var invocation=(EndpointSessionInvocationHandle)record.GetType().GetProperty("Handle")!.GetValue(record)!;
            Assert.False(kernel.AcceptSessionInvocation(service,invocation,false).IsSuccess);
            if(mode=="request-copy")
            {
                var field=pending!.GetType().GetField("Request",Private)!;field.SetValue(pending,originalRequest! with { });
                Assert.False(host.AdvancePendingResult().IsSuccess);Assert.Single(records);
                field.SetValue(pending,originalRequest);
            }
            if(mode=="route-copy")
            {
                var key=Assert.Single(routes.Keys.Cast<object>());routes[key]=Clone(record);
                Assert.False(host.AdvancePendingResult().IsSuccess);Assert.Single(records);
                routes[key]=record;
            }
            Assert.True(host.AdvancePendingResult().IsSuccess);
        }
        if(mode=="delivery-copy")
        {
            await Assert.ThrowsAnyAsync<Exception>(async()=>await reply!);
            Assert.Single(records);Assert.True(host.HasPendingResult);
            Assert.False(host.AdvancePendingResult().IsSuccess);
            record.GetType().GetProperty("OriginalDeliveredRequest")!.SetValue(record,originalRequest);
            Assert.True(host.AdvancePendingResult().IsSuccess);
        }
        if(mode=="delayed")
        {
            Assert.Single(records); // Cancellation publication is not local consumption.
            Assert.Equal(ResponsePublicationStatus.Cancelled,kernel.ReceiveResponse(owner,channel).Value!.Status);
        }
        else await Assert.ThrowsAnyAsync<Exception>(async()=>await reply!.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(records);Assert.Empty(routes);Assert.False(host.HasPendingResult);
        Assert.True(kernel.ReadOriginalSessionCancelledLifetime(lifetime!,
            (EndpointSessionInvocationHandle)record.GetType().GetProperty("Handle")!.GetValue(record)!,service,originalRequest!).IsSuccess);
        Assert.False(kernel.ReadOriginalSessionCancelledLifetime((EndpointSessionInvocationRegistry.OriginalPublication)Clone(lifetime!),
            (EndpointSessionInvocationHandle)record.GetType().GetProperty("Handle")!.GetValue(record)!,service,originalRequest!).IsSuccess);
        Assert.Equal(1024UL,Used());Assert.Equal(1,spy.Calls["BindOriginalVirtualIo"]);
        Assert.Equal(0,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));Assert.Equal(0,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
        var callerBytes=new byte[]{0xA1,0xA2};
        var refusedWrite=new KernelVirtualIoTransferRequest(io with {Generation=io.Generation+1},0,true,2,callerBytes);
        var privateInput=refusedWrite.Input!;
        Assert.False(kernel.TransferVirtualIo(owner,refusedWrite).IsSuccess);
        Assert.Null(refusedWrite.Input);Assert.Equal(new byte[2],privateInput);
        Assert.Equal(new byte[]{0xA1,0xA2},callerBytes); // Only private request custody is cleared.
        // Refusing a different request never contains the independent live IO.
        var writing=new KernelVirtualIoTransferRequest(io,0,true,1,new byte[]{87});
        var reentered=false;
        stage.Observe=incarnation=>
        {
            var locals=(IDictionary)kernel.GetType().GetField("_virtualIo",Private)!.GetValue(kernel)!;
            var local=Assert.Single(locals.Values.Cast<object>());
            var facts=((IList)local.GetType().GetProperty("Transfers",Private)!.GetValue(local)!).Cast<object>().ToArray();
            if(reentered || !facts.Any(f=>ReferenceEquals(f.GetType().GetField("Request",Private)!.GetValue(f),writing)))return incarnation;
            reentered=true;
            Assert.False(kernel.TransferVirtualIo(owner,writing).IsSuccess);
            Assert.Equal(new byte[]{87},writing.Input); // Original in-flight preparation owns these bytes.
            return incarnation;
        };
        Assert.True(kernel.TransferVirtualIo(owner,writing).IsSuccess);Assert.True(reentered);stage.Observe=null;
        Assert.Null(writing.Input);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0UL,Used());
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        Assert.Equal(1,spy.Calls["BindOriginalVirtualIo"]);Assert.Equal(1,spy.Calls["TransferOriginalVirtualIo"]);Assert.Equal(1,spy.Calls["CloseOriginalVirtualIo"]);
        Assert.Equal(0x6C,foreign.Span[0]);Assert.True(kernel.Processes.Resolve(service).IsSuccess);
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
    }
    private static object Clone(object value)=>typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(value,null)!;
}
