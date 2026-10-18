using System.Collections;
using System.Reflection;
using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip.Virtualization;
using SingPlus.Sip.Native;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;
namespace SingPlus.Tests.Platform;
public sealed class NativeTransferRawCustodyConsumerTests
{
    private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    public class CloseSpy : ExactVirtualIoBackingConsumerTests.BackingSpy
    {
        public bool LoseContinuationReply, LoseClosureQuery, LoseDeviceQuery;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(LoseClosureQuery && method!.Name=="ResolveOriginalVirtualIoClosure" || LoseDeviceQuery && method!.Name=="ResolveOriginalParentDeviceClosure")throw new IOException("Original continued closure query lost.");
            var result=base.Invoke(method,args);
            if(LoseContinuationReply && (method!.Name=="ContinueOriginalVirtualIoClose" && result is ExternalOriginalVirtualIoClosure {NoFurtherUse:true} || method!.Name=="ContinueOriginalParentDeviceClose" && result is ExternalOriginalParentDeviceClosure {NoFurtherUse:true}))
            {
                LoseContinuationReply=false;
                if(method!.Name=="ContinueOriginalParentDeviceClose")LoseDeviceQuery=true;else LoseClosureQuery=true;
                throw new IOException("Original continuation reply lost after snapshot closure.");
            }
            return result;
        }
    }
    [Theory]
    [InlineData(false,false,false,null)] [InlineData(true,false,false,null)] [InlineData(true,true,false,null)]
    [InlineData(true,false,true,null)] [InlineData(true,true,true,null)]
    [InlineData(false,false,false,"Closure")] [InlineData(false,false,false,"Closure-copy")]
    [InlineData(false,false,false,"DeviceClosure")] [InlineData(false,false,false,"DeviceClosure-copy")]
    public void LostNativeReadReplyAndCopiedQueryRetainOriginalSnapshotAndChargesUntilExactRecovery(bool prematureClose,bool loseReply,bool deviceStage,string? custody)
    {
        var native=new HybridCpuExternalRuntime();
        var proxy=DispatchProxy.Create<ArtifactReleaseIoAdmissionTests.IRuntime,CloseSpy>();
        var spy=(CloseSpy)(object)proxy;spy.Inner=native;
        var root=DispatchProxy.Create<ExactArtifactReleaseConsumerTests.IOriginalRootRuntime,ExactArtifactReleaseConsumerTests.RootSpy>();
        var rootSpy=(ExactArtifactReleaseConsumerTests.RootSpy)(object)root;rootSpy.Inner=native;
        var adapter=new HybridCpuExecutableChildAdapter(root,proxy);
        var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(),adapter));
        var (process,owner)=TestFixtures.Create(kernel,996,1996);var service=TestFixtures.Create(kernel,997,1997).Handle;
        var foreign=kernel.AllocateBuffer<byte>(service,16).Value!;foreign.Span[0]=0x7E;
        var admin=TestFixtures.Create(kernel,99,999).Handle;
        var cap=kernel.MintCapability(new(999),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(admin,cap,owner,"native-raw",[new(ServiceBudgetDimension.OwnedMemoryBytes,1026)]).Value!.ProcessBudget;
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        var region=kernel.AllocateBuffer<byte>(owner,512).Value!;
        var create=kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var domain=kernel.CreateVirtualDomain(owner,create,new(1,4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess);
        var binding=kernel.QueryVirtualDomainPlatformBinding(owner,domain.Domain).Value!;
        var deviceCap=kernel.MintCapability(process.DomainId,owner,ResourceKind.Device,"device/native-raw",CapabilityRights.Configure|CapabilityRights.Read|CapabilityRights.Write).Value!.CapabilityId;
        var device=kernel.BindPlatformDevice(owner,binding,deviceCap,PlatformDeviceRights.Read|PlatformDeviceRights.Write).Value!;
        var io=kernel.BindVirtualIo(owner,domain.Domain,device,new(PlatformDeviceRights.Read|PlatformDeviceRights.Write,512)).Value!;
        Assert.True(kernel.TransferVirtualIo(owner,new(io,0,true,2,[71,73])).IsSuccess);
        var protocol=VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"native-raw",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(owner,descriptor,[]).Value;var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        var client=VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        object? fact=null;FieldInfo? rawField=null;ExternalOriginalVirtualIoTransferReceipt? produced=null;object? nativeIo=null;
        spy.Fault=deviceStage?"kernel-read-query-loss":"transfer-reply-lost";
        spy.After=name=>
        {
            if(name!="TransferOriginalVirtualIo" || produced is not null)return;
            var owners=(IDictionary)native.GetType().GetField("childRegistry",Hidden)!.GetValue(native)!;
            var child=Assert.Single(owners.Values.Cast<object>());
            nativeIo=Assert.Single(((IEnumerable)child.GetType().GetProperty("OriginalVirtualIo")!.GetValue(child)!).Cast<object>());
            fact=((IEnumerable)nativeIo.GetType().GetProperty("Transfers")!.GetValue(nativeIo)!).Cast<object>().Single(f=>!((ExternalOriginalVirtualIoTransferRequest)f.GetType().GetField("Request")!.GetValue(f)!).Write);
            rawField=fact.GetType().GetField("Raw")!;produced=(ExternalOriginalVirtualIoTransferReceipt)rawField.GetValue(fact)!;
            if(!deviceStage)rawField.SetValue(fact,typeof(object).GetMethod("MemberwiseClone",Hidden)!.Invoke(produced,null));
        };
        var read=client.TransferIoAsync(new(io.Id,io.Generation,0,false,2,default)).AsTask();
        Assert.False(host.ProcessNext().IsSuccess);Assert.ThrowsAny<Exception>(()=>read.GetAwaiter().GetResult());
        Assert.NotNull(produced);Assert.Equal(new byte[]{71,73},produced.CopyReadData());Assert.Equal(1026UL,Used());
        if(!deviceStage)Assert.Null(native.ResolveOriginalVirtualIoTransfer(produced.Request));
        var nativeDevice=nativeIo!.GetType().GetProperty("Device")!.GetValue(nativeIo)!;
        var backing=(byte[])nativeDevice.GetType().GetField("Backing")!.GetValue(nativeDevice)!;
        var corrupted=false;
        if(deviceStage)
        {
            spy.Fault="exact";spy.After=null;
            rootSpy.BeforeQuery=()=>
            {
                if(corrupted || nativeIo.GetType().GetField("Closure")!.GetValue(nativeIo) is null)return;
                corrupted=true;Assert.False(Monitor.IsEntered(adapter.GetType().GetField("sync",Hidden)!.GetValue(adapter)!));
                rawField!.SetValue(fact,typeof(object).GetMethod("MemberwiseClone",Hidden)!.Invoke(produced,null));
                Assert.False(kernel.CloseVirtualIo(io).IsSuccess);
            };
        }
        if(prematureClose)Assert.False(kernel.CloseVirtualIo(io).IsSuccess);Assert.Equal(1026UL,Used());
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        Assert.Equal(2UL,nativeIo!.GetType().GetField("TransfersIssued")!.GetValue(nativeIo));
        Assert.Equal(2,spy.Calls["TransferOriginalVirtualIo"]);Assert.Equal(prematureClose?1:0,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
        if(prematureClose)Assert.NotNull(nativeIo.GetType().GetField("CloseRequest")!.GetValue(nativeIo));
        else Assert.Null(nativeIo.GetType().GetField("CloseRequest")!.GetValue(nativeIo));
        if(deviceStage)Assert.NotNull(nativeIo.GetType().GetField("Closure")!.GetValue(nativeIo));
        else Assert.Null(nativeIo.GetType().GetField("Closure")!.GetValue(nativeIo));
        if(deviceStage){Assert.True(corrupted);Assert.Throws<InvalidOperationException>(()=>produced.CopyReadData());Assert.Equal(new byte[]{71,73},backing.Take(2).ToArray());}
        else Assert.Equal(new byte[]{71,73},produced.CopyReadData());
        Assert.Equal(0x7E,foreign.Span[0]);
        rawField!.SetValue(fact,produced);spy.Fault="exact";spy.After=null;
        Assert.Same(produced,native.ResolveOriginalVirtualIoTransfer(produced.Request));
        object? sdkOwner=null,closureOriginal=null;FieldInfo? closureRoute=null;var finalQueries=0;
        if(custody is not null)
        {
            sdkOwner=Assert.Single(((IDictionary)adapter.GetType().GetField("originalIo",Hidden)!.GetValue(adapter)!).Values.Cast<object>());
            rootSpy.BeforeQuery=()=>
            {
                if(sdkOwner.GetType().GetField("DeviceClosure")!.GetValue(sdkOwner) is null || ++finalQueries!=2)return;
                Assert.False(Monitor.IsEntered(adapter.GetType().GetField("sync",Hidden)!.GetValue(adapter)!));
                closureRoute=sdkOwner.GetType().GetField(custody.Replace("-copy",""))!;closureOriginal=closureRoute.GetValue(sdkOwner);Assert.NotNull(closureOriginal);
                closureRoute.SetValue(sdkOwner,custody.EndsWith("-copy",StringComparison.Ordinal)?typeof(object).GetMethod("MemberwiseClone",Hidden)!.Invoke(closureOriginal,null):null);
                Assert.False(kernel.CloseVirtualIo(io).IsSuccess);
            };
        }
        spy.LoseContinuationReply=loseReply;
        var closed=kernel.CloseVirtualIo(io);
        if(custody is not null)
        {
            Assert.NotNull(closureRoute);Assert.Equal((false,1026UL,false),(closed.IsSuccess,Used(),(bool)sdkOwner!.GetType().GetField("Consumed")!.GetValue(sdkOwner)!));
            Assert.False((bool)sdkOwner!.GetType().GetField("Consumed")!.GetValue(sdkOwner)!);
            Assert.All(backing,value=>Assert.Equal(0,value));Assert.Throws<InvalidOperationException>(()=>produced.CopyReadData());
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);Assert.Equal(0x7E,foreign.Span[0]);
            rootSpy.BeforeQuery=null;closureRoute!.SetValue(sdkOwner,closureOriginal);closed=kernel.CloseVirtualIo(io);
        }
        if(loseReply)
        {
            Assert.False(closed.IsSuccess);Assert.Equal(1026UL,Used());Assert.Throws<InvalidOperationException>(()=>produced.CopyReadData());
            var originalClosure=nativeIo.GetType().GetField("Closure")!.GetValue(nativeIo);Assert.NotNull(originalClosure);
            Assert.False(kernel.CloseVirtualIo(io).IsSuccess);Assert.Equal(1026UL,Used());
            var deviceTerminal=nativeDevice.GetType().GetField("Closure")!.GetValue(nativeDevice);
            if(deviceStage){Assert.NotNull(deviceTerminal);Assert.All(backing,value=>Assert.Equal(0,value));}
            spy.LoseClosureQuery=false;spy.LoseDeviceQuery=false;closed=kernel.CloseVirtualIo(io);
            Assert.Same(originalClosure,nativeIo.GetType().GetField("Closure")!.GetValue(nativeIo));
            if(deviceStage)Assert.Same(deviceTerminal,nativeDevice.GetType().GetField("Closure")!.GetValue(nativeDevice));
        }
        Assert.True(closed.IsSuccess,closed.Message);
        if(custody is not null)
        {
            var originalAdmission=(NeutralOriginalVirtualIoCloseRequest)sdkOwner!.GetType().GetField("CloseAdmission")!.GetValue(sdkOwner)!;
            closureRoute!.SetValue(sdkOwner,custody.EndsWith("-copy",StringComparison.Ordinal)?typeof(object).GetMethod("MemberwiseClone",Hidden)!.Invoke(closureOriginal,null):null);
            Assert.False(adapter.ContinueOriginalVirtualIoClose(originalAdmission).IsSuccess);Assert.Equal(512UL,Used());
            closureRoute.SetValue(sdkOwner,closureOriginal);Assert.True(adapter.ContinueOriginalVirtualIoClose(originalAdmission).IsSuccess);
        }
        Assert.Equal(prematureClose && !deviceStage?2:0,spy.Calls.GetValueOrDefault("ContinueOriginalVirtualIoClose"));
        Assert.Equal(deviceStage?2:0,spy.Calls.GetValueOrDefault("ContinueOriginalParentDeviceClose"));
        Assert.All(backing,value=>Assert.Equal(0,value));
        Assert.Throws<InvalidOperationException>(()=>produced.CopyReadData());Assert.Equal(512UL,Used());
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0UL,Used());
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        Assert.Equal(2,spy.Calls["TransferOriginalVirtualIo"]);Assert.Equal(1,spy.Calls["CloseOriginalVirtualIo"]);Assert.Equal(1,spy.Calls["CloseOriginalParentDevice"]);Assert.Equal(0x7E,foreign.Span[0]);
    }
}
