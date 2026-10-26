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
using SingPlus.Sip;
namespace SingPlus.Tests.Platform;
public sealed class NativeTransferRawCustodyConsumerTests
{
    private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    public interface ISourceRuntime : INeutralDomainRuntime, INeutralOriginalDeviceLoanProvider, INeutralOriginalBindOutcomeProvider, INeutralOriginalDomainCloseContinuationProvider { }
    public class SourceSpy : DispatchProxy
    {
        public NeutralDomainRuntimeFacade Inner = new();
        public Action? BeforeSource;
        public bool LoseRootContinuationReply,LoseRootQuery;
        public int RootCloseCalls,RootContinuationCalls;
        public NeutralDomainCloseResult? OriginalRootResult;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if(method!.Name=="get_MappingClosureIncarnation")BeforeSource?.Invoke();
            if(method.Name=="ResolveDomainClose" && LoseRootQuery)throw new IOException("Original root terminal query lost.");
            if(method.Name=="Close" && args is [NeutralOriginalDomainCloseRequest])RootCloseCalls++;
            if(method.Name=="ContinueOriginalDomainClose")RootContinuationCalls++;
            try
            {
                object? raw =method.Invoke(Inner,args);
                if(method.Name=="ContinueOriginalDomainClose" && raw is NeutralOriginalDomainCloseObservation {Observation.Decision:NeutralDomainCloseDecision.Closed} closed)
                {
                    OriginalRootResult=closed.Observation;
                    if(LoseRootContinuationReply){LoseRootContinuationReply=false;LoseRootQuery=true;throw new IOException("Root continuation reply lost after effect.");}
                }
                return raw;
            }
            catch(TargetInvocationException exception) when(exception.InnerException is not null)
            {global::System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();throw;}
        }
    }
    public class CloseSpy : ExactVirtualIoBackingConsumerTests.BackingSpy
    {
        public bool LoseContinuationReply, LoseClosureQuery, LoseDeviceQuery;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(LoseClosureQuery && method!.Name=="ResolveOriginalVirtualIoClosure" || LoseDeviceQuery && method!.Name=="ResolveOriginalParentDeviceClosure")throw new IOException("Original continued closure query lost.");
            object? result =base.Invoke(method,args);
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
    [InlineData(false,false,false,"provider-forged")]
    [InlineData(false,false,false,"provider-loss")] [InlineData(false,false,false,"provider-copy")]
    [InlineData(false,false,false,"bridge-raw-copy")] [InlineData(false,false,false,"bridge-raw-loss")]
    [InlineData(false,false,false,"bridge-closure-copy")] [InlineData(false,false,false,"bridge-closure-loss")]
    [InlineData(false,false,false,"close-noeffect-copy")] [InlineData(false,false,false,"close-noeffect-raw-copy")]
    [InlineData(true,false,false,"native-close-request-loss")] [InlineData(true,false,false,"native-close-request-copy")]
    [InlineData(false,false,false,"kernel-prepare-noeffect-loss")]
    [InlineData(false,false,false,"kernel-prepare-rereserve")]
    [InlineData(false,false,false,"dispatch-invoked-loss")]
    [InlineData(false,false,false,"dispatch-route-loss")]
    [InlineData(false,false,false,"dispatch-route-copy")]
    public void LostNativeReadReplyAndCopiedQueryRetainOriginalSnapshotAndChargesUntilExactRecovery(bool prematureClose,bool loseReply,bool deviceStage,string? custody)
    {
        HybridCpuExternalRuntime native =new HybridCpuExternalRuntime();
        ArtifactReleaseIoAdmissionTests.IRuntime proxy =DispatchProxy.Create<ArtifactReleaseIoAdmissionTests.IRuntime,CloseSpy>();
        CloseSpy spy =(CloseSpy)(object)proxy;spy.Inner=native;
        ExactArtifactReleaseConsumerTests.IOriginalRootRuntime root =DispatchProxy.Create<ExactArtifactReleaseConsumerTests.IOriginalRootRuntime,ExactArtifactReleaseConsumerTests.RootSpy>();
        ExactArtifactReleaseConsumerTests.RootSpy rootSpy =(ExactArtifactReleaseConsumerTests.RootSpy)(object)root;rootSpy.Inner=native;
        HybridCpuExecutableChildAdapter adapter =new HybridCpuExecutableChildAdapter(root,proxy);
        bool providerCustody =custody?.StartsWith("provider-",StringComparison.Ordinal)==true;
        bool bridgeCustody =custody?.StartsWith("bridge-",StringComparison.Ordinal)==true;
        ISourceRuntime model =DispatchProxy.Create<ISourceRuntime,SourceSpy>(); SourceSpy sourceSpy =(SourceSpy)(object)model;
        HybridCpuPlatformAuthorityProvider provider =new HybridCpuPlatformAuthorityProvider(model,adapter);
        RuntimeKernel kernel =new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel,996,1996); ProcessHandle service =TestFixtures.Create(kernel,997,1997).Handle;
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(service,16).Value!;foreign.Span[0]=0x7E;
        ProcessHandle admin =TestFixtures.Create(kernel,99,999).Handle;
        CapabilityId cap =kernel.MintCapability(new(999),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(admin,cap,owner,"native-raw",[new(ServiceBudgetDimension.OwnedMemoryBytes,1026)]).Value!.ProcessBudget;
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        OwnedBuffer<byte> region =kernel.AllocateBuffer<byte>(owner,512).Value!;
        CapabilityId create =kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain =kernel.CreateVirtualDomain(owner,create,new(1,4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess);
        PlatformDomainBinding binding =kernel.QueryVirtualDomainPlatformBinding(owner,domain.Domain).Value!;
        CapabilityId deviceCap =kernel.MintCapability(process.DomainId,owner,ResourceKind.Device,"device/native-raw",CapabilityRights.Configure|CapabilityRights.Read|CapabilityRights.Write).Value!.CapabilityId;
        PlatformDeviceLease device =kernel.BindPlatformDevice(owner,binding,deviceCap,PlatformDeviceRights.Read|PlatformDeviceRights.Write).Value!;
        VirtualIoBinding io =kernel.BindVirtualIo(owner,domain.Domain,device,new(PlatformDeviceRights.Read|PlatformDeviceRights.Write,512)).Value!;
        Assert.True(kernel.TransferVirtualIo(owner,new(io,0,true,2,[71,73])).IsSuccess);
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"native-raw",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(owner,descriptor,[]).Value; RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        IVirtualizationService client =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        if (custody?.StartsWith("close-noeffect-", StringComparison.Ordinal) == true)
        {
            object child = Assert.Single(((IDictionary)native.GetType().GetField("childRegistry", Hidden)!.GetValue(native)!).Values.Cast<object>());
            var nativeAdmission = (ExternalChildDomainCreateReceipt)child.GetType().GetProperty("OriginalAdmission")!.GetValue(child)!;
            var refusedClose = native.CloseOriginalChild(new(nativeAdmission, new(new(Guid.NewGuid()), new(990))))!;
            Assert.True(refusedClose.NoEffect);
            object closeFact = child.GetType().GetProperty("OriginalClose")!.GetValue(child)!;
            PropertyInfo slot = closeFact.GetType().GetProperty(custody.EndsWith("raw-copy", StringComparison.Ordinal) ? "Raw" : "Observation")!;
            object original = slot.GetValue(closeFact)!;
            slot.SetValue(closeFact, typeof(object).GetMethod("MemberwiseClone", Hidden)!.Invoke(original, null));
            spy.Fault = "kernel-read-query-loss";
            Task<VirtualIoTransferResponse> refusedRead = client.TransferIoAsync(new(io.Id,io.Generation,0,false,2,default)).AsTask();
            Assert.False(host.ProcessNext().IsSuccess);
            Assert.ThrowsAny<Exception>(() => refusedRead.GetAwaiter().GetResult());
            object transferOwner = Assert.Single(((IEnumerable)child.GetType().GetProperty("OriginalVirtualIo")!.GetValue(child)!).Cast<object>());
            object refusedDevice = transferOwner.GetType().GetProperty("Device")!.GetValue(transferOwner)!;
            byte[] refusedBacking = (byte[])refusedDevice.GetType().GetField("Backing")!.GetValue(refusedDevice)!;
            Assert.Equal(new byte[] {71,73}, refusedBacking.Take(2).ToArray());
            Assert.Equal(2UL, transferOwner.GetType().GetField("TransfersIssued")!.GetValue(transferOwner));
            object refusalFact = ((IEnumerable)transferOwner.GetType().GetProperty("Transfers")!.GetValue(transferOwner)!).Cast<object>().Last();
            var noEffect = (ExternalOriginalVirtualIoTransferReceipt)refusalFact.GetType().GetField("Raw")!.GetValue(refusalFact)!;
            Assert.True(noEffect.NoEffect); Assert.False(noEffect.Completed);
            Assert.Same(noEffect, native.ResolveOriginalVirtualIoTransfer(noEffect.Request));
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1026UL, Used());
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            slot.SetValue(closeFact, original);
            // The lost query still retains charges even after current admission is repaired.
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1026UL, Used());
            spy.Fault = "exact";
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(0UL, Used());
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            Assert.Equal(2, spy.Calls["TransferOriginalVirtualIo"]);
            Assert.Equal(2UL, transferOwner.GetType().GetField("TransfersIssued")!.GetValue(transferOwner));
            Assert.Equal(0x7E, foreign.Span[0]);
            return;
        }
        object? fact=null;FieldInfo? rawField=null;ExternalOriginalVirtualIoTransferReceipt? produced=null;object? nativeIo=null;
        spy.Fault=deviceStage?"kernel-read-query-loss":"transfer-reply-lost";
        spy.After=name=>
        {
            if(name!="TransferOriginalVirtualIo" || produced is not null)return;
            IDictionary owners =(IDictionary)native.GetType().GetField("childRegistry",Hidden)!.GetValue(native)!;
            object child =Assert.Single(owners.Values.Cast<object>());
            nativeIo=Assert.Single(((IEnumerable)child.GetType().GetProperty("OriginalVirtualIo")!.GetValue(child)!).Cast<object>());
            fact=((IEnumerable)nativeIo.GetType().GetProperty("Transfers")!.GetValue(nativeIo)!).Cast<object>().Single(f=>!((ExternalOriginalVirtualIoTransferRequest)f.GetType().GetField("Request")!.GetValue(f)!).Write);
            rawField=fact.GetType().GetField("Raw")!;produced=(ExternalOriginalVirtualIoTransferReceipt)rawField.GetValue(fact)!;
            if(!deviceStage)rawField.SetValue(fact,typeof(object).GetMethod("MemberwiseClone",Hidden)!.Invoke(produced,null));
        };
        Task<VirtualIoTransferResponse> read =client.TransferIoAsync(new(io.Id,io.Generation,0,false,2,default)).AsTask();
        Assert.False(host.ProcessNext().IsSuccess);Assert.ThrowsAny<Exception>(()=>read.GetAwaiter().GetResult());
        Assert.NotNull(produced);Assert.Equal(new byte[]{71,73},produced.CopyReadData());Assert.Equal(1026UL,Used());
        if(!deviceStage)Assert.Null(native.ResolveOriginalVirtualIoTransfer(produced.Request));
        object nativeDevice =nativeIo!.GetType().GetProperty("Device")!.GetValue(nativeIo)!;
        byte[] backing =(byte[])nativeDevice.GetType().GetField("Backing")!.GetValue(nativeDevice)!;
        if(custody?.StartsWith("dispatch-",StringComparison.Ordinal)==true)
        {
            object bridgeIo=Assert.Single(((IEnumerable)kernel.PlatformAuthority.GetType().GetField("_virtualIoOwners",Hidden)!.GetValue(kernel.PlatformAuthority)!).Cast<object>());
            object dispatched=((IEnumerable)bridgeIo.GetType().GetProperty("Transfers")!.GetValue(bridgeIo)!).Cast<object>()
                .Single(t=>!((PlatformOriginalVirtualIoTransferRequest)t.GetType().GetField("Request")!.GetValue(t)!).Write);
            var original=(PlatformOriginalVirtualIoTransferRequest)dispatched.GetType().GetField("Request")!.GetValue(dispatched)!;
            Assert.Null(dispatched.GetType().GetField("Raw")!.GetValue(dispatched));
            FieldInfo invoked=dispatched.GetType().GetField("Invoked")!;
            Assert.True((bool)invoked.GetValue(dispatched)!);invoked.SetValue(dispatched,false);
            FieldInfo dispatch=dispatched.GetType().GetField("Dispatch")!;
            object originalDispatch=dispatch.GetValue(dispatched)!;
            if(custody!="dispatch-invoked-loss")dispatch.SetValue(dispatched,custody=="dispatch-route-loss"?null:typeof(object).GetMethod("MemberwiseClone",Hidden)!.Invoke(originalDispatch,null));
            int localCalls=0;
            var platformBinding=(PlatformVirtualIoBinding)bridgeIo.GetType().GetProperty("Binding")!.GetValue(bridgeIo)!;
            Assert.False(kernel.PlatformAuthority.ConsumeUninvokedVirtualIoTransfer(platformBinding,original,()=>{localCalls++;return KernelResult.Ok();}).IsSuccess);
            Assert.Equal(0,localCalls);Assert.Equal(1026UL,Used());
            Assert.Equal(new byte[]{71,73},produced.CopyReadData());Assert.Equal(0x7E,foreign.Span[0]);
            Assert.Equal(2,spy.Calls["TransferOriginalVirtualIo"]);
            Assert.False(kernel.PlatformAuthority.ResolveVirtualIoTransfer(platformBinding,original).IsSuccess);
            Assert.False(kernel.PlatformAuthority.InvokeVirtualIoTransfer(platformBinding,original).IsSuccess);
            Assert.Equal(2,spy.Calls["TransferOriginalVirtualIo"]);
            Assert.Same(originalDispatch,dispatched.GetType().GetField("OriginalDispatch")!.GetValue(dispatched));
            dispatch.SetValue(dispatched,originalDispatch);invoked.SetValue(dispatched,true);custody=null;
        }
        if(custody?.StartsWith("kernel-prepare-",StringComparison.Ordinal)==true)
        {
            object local=Assert.Single(((IDictionary)kernel.GetType().GetField("_virtualIo",Hidden)!.GetValue(kernel)!).Values.Cast<object>());
            object pending=((IList)local.GetType().GetProperty("Transfers",Hidden)!.GetValue(local)!).Cast<object>()
                .Single(t=>!((KernelVirtualIoTransferRequest)t.GetType().GetField("Request",Hidden)!.GetValue(t)!).Write);
            FieldInfo platform=pending.GetType().GetField("Platform",Hidden)!;
            object originalPlatform=platform.GetValue(pending)!;
            Assert.Null(pending.GetType().GetField("Raw",Hidden)!.GetValue(pending));
            platform.SetValue(pending,null);
            if(custody=="kernel-prepare-rereserve")pending.GetType().GetField("PreparationStarted",Hidden)!.SetValue(pending,false);
            else
            {
                pending.GetType().GetField("OwnPrepareNoEffect",Hidden)!.SetValue(pending,true);
                pending.GetType().GetField("LocalNoEffect",Hidden)!.SetValue(pending,true);
            }
            KernelVirtualIoTransferRequest original=(KernelVirtualIoTransferRequest)pending.GetType().GetField("Request",Hidden)!.GetValue(pending)!;
            Assert.False(kernel.TransferVirtualIo(owner,original).IsSuccess);
            Assert.Equal(1026UL,Used()); // Native snapshot remains live: metadata cannot refund its charge.
            Assert.Equal(new byte[]{71,73},produced.CopyReadData());Assert.Equal(new byte[]{71,73},backing.Take(2).ToArray());
            object registry=kernel.GetType().GetField("_endpointSessionInvocationRegistry",Hidden)!.GetValue(kernel)!;
            object retained=Assert.Single(((IDictionary)registry.GetType().GetField("_ownedRecords",Hidden)!.GetValue(registry)!).Values.Cast<object>());
            Assert.False((bool)retained.GetType().GetProperty("VirtualIoTransferNoEffect")!.GetValue(retained)!);
            Assert.Equal(2,spy.Calls["TransferOriginalVirtualIo"]);Assert.Equal(0,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
            platform.SetValue(pending,originalPlatform);
            custody=null; // Continue the existing exact recovery control, not SDK-closure corruption.
        }
        bool corrupted =false;
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
        if(custody?.StartsWith("native-close-request-",StringComparison.Ordinal)==true)
        {
            FieldInfo slot=nativeIo.GetType().GetField("CloseRequest")!;
            ExternalOriginalVirtualIoCloseRequest original=Assert.IsType<ExternalOriginalVirtualIoCloseRequest>(slot.GetValue(nativeIo));
            ExternalOriginalVirtualIoCloseRequest foreignClose=new(original.Admission,original.Binding,new(new(Guid.NewGuid()),new(993)));
            slot.SetValue(nativeIo,custody.EndsWith("loss",StringComparison.Ordinal)?null:foreignClose);
            Assert.Null(native.TransferOriginalVirtualIo(new(original.Admission,new(new(Guid.NewGuid()),new(994)),0,true,1,[0xEE])));
            Assert.False(native.TransferOriginalVirtualIo(original.Admission,0,[0xEE],true));
            Assert.Null(native.CloseOriginalVirtualIo(foreignClose));Assert.Null(native.ContinueOriginalVirtualIoClose(foreignClose));
            Assert.Null(native.ResolveOriginalVirtualIoClosure(foreignClose));
            Assert.Null(nativeIo.GetType().GetField("Closure")!.GetValue(nativeIo));
            Assert.Equal(new byte[]{71,73},produced.CopyReadData());Assert.Equal(new byte[]{71,73},backing.Take(2).ToArray());
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(1026UL,Used());
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);Assert.Equal(0x7E,foreign.Span[0]);
            slot.SetValue(nativeIo,original);
            bool finalDamage=false;
            rootSpy.BeforeQuery=()=>
            {
                if(finalDamage || nativeIo.GetType().GetField("Closure")!.GetValue(nativeIo) is null)return;
                finalDamage=true;slot.SetValue(nativeIo,custody.EndsWith("loss",StringComparison.Ordinal)?null:foreignClose);
                Assert.Null(native.CloseOriginalParentDevice(original.Admission.Device));
            };
            Assert.False(kernel.CloseVirtualIo(io).IsSuccess);Assert.True(finalDamage);Assert.Equal(1026UL,Used());
            Assert.Equal(new byte[]{71,73},backing.Take(2).ToArray());Assert.Equal(0x7E,foreign.Span[0]);
            rootSpy.BeforeQuery=null;slot.SetValue(nativeIo,original);
            KernelResult repairedClose=kernel.CloseVirtualIo(io);Assert.True(repairedClose.IsSuccess,repairedClose.Message);Assert.Equal(512UL,Used());
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0UL,Used());
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            Assert.All(backing,b=>Assert.Equal(0,b));Assert.Equal(2UL,nativeIo.GetType().GetField("TransfersIssued")!.GetValue(nativeIo));
            Assert.Equal(2,spy.Calls["TransferOriginalVirtualIo"]);Assert.Equal(1,spy.Calls["CloseOriginalVirtualIo"]);
            Assert.Throws<InvalidOperationException>(()=>produced.CopyReadData());Assert.Equal(0x7E,foreign.Span[0]);return;
        }
        object? sdkOwner=null,closureOriginal=null;FieldInfo? closureRoute=null; int finalQueries =0;
        object? providerOwner=null,providerCompletion=null;PropertyInfo? completionRoute=null;
        object? bridgeOwner=null,bridgeFact=null,bridgeOriginal=null;PropertyInfo? bridgeRoute=null;
        if(bridgeCustody)
        {
            bridgeOwner=Assert.Single(((IEnumerable)kernel.PlatformAuthority.GetType().GetField("_virtualIoOwners",Hidden)!.GetValue(kernel.PlatformAuthority)!).Cast<object>());
            kernel.BeforeVirtualIoClosureLocalCommit=()=>
            {
                kernel.BeforeVirtualIoClosureLocalCommit=null;
                Assert.False(Monitor.IsEntered(kernel.PlatformAuthority.GetType().GetField("_secureDomainLifecycleGate",Hidden)!.GetValue(kernel.PlatformAuthority)!));
                bridgeFact=bridgeOwner.GetType().GetProperty("CloseInvocation")!.GetValue(bridgeOwner)!;
                object target =custody!.StartsWith("bridge-raw",StringComparison.Ordinal)?bridgeFact:bridgeOwner;
                bridgeRoute=target.GetType().GetProperty(ReferenceEquals(target,bridgeFact)?"Observation":"LastClosureReceipt")!;
                bridgeOriginal=bridgeRoute.GetValue(target);Assert.NotNull(bridgeOriginal);
                if(custody=="bridge-raw-copy" && bridgeOriginal is PlatformAuthorityResult<PlatformVirtualIoClosureReceipt> raw)
                {
                    bridgeRoute.SetValue(target,raw with {Value=raw.Value! with { }});
                }
                else bridgeRoute.SetValue(target,custody.EndsWith("copy",StringComparison.Ordinal)?typeof(object).GetMethod("MemberwiseClone",Hidden)!.Invoke(bridgeOriginal,null):null);
                Assert.False(kernel.CloseVirtualIo(io).IsSuccess);
            };
        }
        if(providerCustody)
        {
            providerOwner=Assert.Single(((IDictionary)provider.GetType().GetField("_originalProviderVirtualIo",Hidden)!.GetValue(provider)!).Values.Cast<object>());
            completionRoute=providerOwner.GetType().GetProperty("Completion")!;
            if(custody=="provider-forged")spy.Fault="close-query-throw";
            sourceSpy.BeforeSource=()=>
            {
                if(providerCompletion is not null || completionRoute.GetValue(providerOwner) is not { } raw)return;
                providerCompletion=raw;
                Assert.False(Monitor.IsEntered(provider.GetType().GetField("_childCreateCommitGate",Hidden)!.GetValue(provider)!));
                if(custody=="provider-forged")
                {
                    NeutralVirtualIoLease lease =(NeutralVirtualIoLease)providerOwner.GetType().GetProperty("Neutral")!.GetValue(providerOwner)!;
                    NeutralVirtualizationResult<NeutralVirtualIoCloseReceipt> forged =new NeutralVirtualizationResult<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Success,
                        new(lease.Handle,lease.Epoch,lease.ChildLease.Handle,lease.ChildLease.Epoch,lease.ChildLease.ParentLease.Handle,lease.ChildLease.ParentLease.Epoch,true),"");
                    if(raw is NeutralVirtualizationResult<NeutralVirtualIoCloseReceipt>)completionRoute.SetValue(providerOwner,forged);
                    else
                    {
                        object copy =typeof(object).GetMethod("MemberwiseClone",Hidden)!.Invoke(raw,null)!;
                        copy.GetType().GetField("<Result>k__BackingField",Hidden)!.SetValue(copy,forged);
                        completionRoute.SetValue(providerOwner,copy);
                    }
                }
                else completionRoute.SetValue(providerOwner,custody=="provider-copy"?typeof(object).GetMethod("MemberwiseClone",Hidden)!.Invoke(raw,null):null);
                Assert.False(kernel.CloseVirtualIo(io).IsSuccess);
            };
        }
        if(custody is not null && !providerCustody && !bridgeCustody)
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
        KernelResult closed =kernel.CloseVirtualIo(io);
        if(bridgeCustody)
        {
            Assert.NotNull(bridgeOriginal);
            Assert.Equal((false,1026UL,false),(closed.IsSuccess,Used(),(bool)bridgeOwner!.GetType().GetProperty("ClosureConsumed")!.GetValue(bridgeOwner)!));
            Assert.All(backing,value=>Assert.Equal(0,value));Assert.Throws<InvalidOperationException>(()=>produced.CopyReadData());
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);Assert.Equal(0x7E,foreign.Span[0]);
            Assert.False(kernel.CloseVirtualIo(io).IsSuccess);Assert.Equal(1026UL,Used());
            bridgeRoute!.SetValue(custody!.StartsWith("bridge-raw",StringComparison.Ordinal)?bridgeFact:bridgeOwner,bridgeOriginal);
            closed=kernel.CloseVirtualIo(io);
        }
        if(providerCustody)
        {
            Assert.NotNull(providerCompletion);
            Assert.Equal((false,1026UL,false),(closed.IsSuccess,Used(),(bool)providerOwner!.GetType().GetProperty("Consumed")!.GetValue(providerOwner)!));
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            Assert.Equal(0x7E,foreign.Span[0]);Assert.Throws<InvalidOperationException>(()=>produced.CopyReadData());
            if(custody=="provider-forged")Assert.Equal(new byte[]{71,73},backing.Take(2).ToArray());
            else Assert.All(backing,value=>Assert.Equal(0,value));
            sourceSpy.BeforeSource=null;
            Assert.False(kernel.CloseVirtualIo(io).IsSuccess);Assert.Equal(1026UL,Used());
            completionRoute!.SetValue(providerOwner,providerCompletion);spy.Fault="exact";closed=kernel.CloseVirtualIo(io);
        }
        if(custody is not null && !providerCustody && !bridgeCustody)
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
            object? originalClosure =nativeIo.GetType().GetField("Closure")!.GetValue(nativeIo);Assert.NotNull(originalClosure);
            Assert.False(kernel.CloseVirtualIo(io).IsSuccess);Assert.Equal(1026UL,Used());
            object? deviceTerminal =nativeDevice.GetType().GetField("Closure")!.GetValue(nativeDevice);
            if(deviceStage){Assert.NotNull(deviceTerminal);Assert.All(backing,value=>Assert.Equal(0,value));}
            spy.LoseClosureQuery=false;spy.LoseDeviceQuery=false;closed=kernel.CloseVirtualIo(io);
            Assert.Same(originalClosure,nativeIo.GetType().GetField("Closure")!.GetValue(nativeIo));
            if(deviceStage)Assert.Same(deviceTerminal,nativeDevice.GetType().GetField("Closure")!.GetValue(nativeDevice));
        }
        Assert.True(closed.IsSuccess,closed.Message);
        if(bridgeCustody)
        {
            object? target =custody!.StartsWith("bridge-raw",StringComparison.Ordinal)?bridgeFact:bridgeOwner;
            PlatformVirtualIoBinding platformBinding =(PlatformVirtualIoBinding)bridgeOwner!.GetType().GetProperty("Binding")!.GetValue(bridgeOwner)!;
            if(custody=="bridge-raw-copy" && bridgeOriginal is PlatformAuthorityResult<PlatformVirtualIoClosureReceipt> raw)
            {
                bridgeRoute!.SetValue(target,raw with {Value=raw.Value! with { }});
            }
            else bridgeRoute!.SetValue(target,custody.EndsWith("copy",StringComparison.Ordinal)?typeof(object).GetMethod("MemberwiseClone",Hidden)!.Invoke(bridgeOriginal,null):null);
            Assert.False(kernel.PlatformAuthority.RevokeChildVirtualIo(platformBinding).IsSuccess);Assert.Equal(512UL,Used());
            bridgeRoute.SetValue(target,bridgeOriginal);Assert.True(kernel.PlatformAuthority.RevokeChildVirtualIo(platformBinding).IsSuccess);
        }
        if(providerCustody)
        {
            PlatformOriginalVirtualIoCloseRequest originalAdmission =(PlatformOriginalVirtualIoCloseRequest)providerOwner!.GetType().GetProperty("CloseAdmission")!.GetValue(providerOwner)!;
            object? originalCompletion =completionRoute!.GetValue(providerOwner);
            completionRoute.SetValue(providerOwner,custody=="provider-loss"?null:typeof(object).GetMethod("MemberwiseClone",Hidden)!.Invoke(originalCompletion,null));
            Assert.False(provider.ContinueOriginalVirtualIoClose(originalAdmission).IsSuccess);Assert.Equal(512UL,Used());
            completionRoute.SetValue(providerOwner,originalCompletion);Assert.True(provider.ContinueOriginalVirtualIoClose(originalAdmission).IsSuccess);
        }
        if(custody is not null && !providerCustody && !bridgeCustody)
        {
            NeutralOriginalVirtualIoCloseRequest originalAdmission =(NeutralOriginalVirtualIoCloseRequest)sdkOwner!.GetType().GetField("CloseAdmission")!.GetValue(sdkOwner)!;
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
