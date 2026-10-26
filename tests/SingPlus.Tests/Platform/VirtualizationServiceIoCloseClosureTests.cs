using System.Collections;
using System.Reflection;
using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Virtualization;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Tests.Platform;

public sealed class VirtualizationServiceIoCloseClosureTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private sealed class AdmissionClock : TimeProvider
    {
        internal Action? Observe;
        public override DateTimeOffset GetUtcNow(){Observe?.Invoke();return base.GetUtcNow();}
    }
    public interface IReleaseRuntime : ExactVirtualIoBackingConsumerTests.IOriginalBackingRuntime,IHybridCpuOriginalVirtualIoSnapshotReleaseRuntimeV1 { }
    public interface IStageProvider : IPlatformAuthorityProvider,IPlatformFeatureProvider,IPlatformProviderIncarnationSource,
        IPlatformDomainExecutionProvider,IPlatformOriginalAdmissionResolutionProvider,IPlatformOriginalDomainClosureProvider,
        IPlatformOriginalDomainClosureContinuationProvider,IPlatformDomainRevocationNoEffectProvider,IPlatformChildDomainProvider,
        IPlatformChildCreateDispositionProvider,IPlatformOriginalChildCreateProvider,IPlatformOriginalChildCloseProvider,
        IPlatformDeviceLeaseProvider,IPlatformOriginalDeviceProvider,IPlatformDeviceAdmissionNoEffectProvider,IPlatformDeviceRevocationNoEffectProvider,
        IPlatformVirtualIoProvider,IPlatformVirtualIoPrivateBackingProvider,IPlatformOriginalVirtualIoBindProvider,
        IPlatformOriginalVirtualIoCloseProvider,IPlatformOriginalVirtualIoTransferProvider { }
    public interface IReadStageProvider : IStageProvider,IPlatformOriginalVirtualIoSnapshotReleaseProvider,IPlatformOriginalVirtualIoSnapshotReleaseContinuationProvider { }
    public interface INeutralReleaseStage : INeutralChildDomainProvider,INeutralRuntimeFeatureProvider,
        INeutralOriginalChildCreateProvider,INeutralOriginalChildCloseProvider,INeutralOriginalChildDrainProvider,
        INeutralExternalParentClosureProvider,INeutralOriginalExternalParentBindNoEffectProvider,
        INeutralGuestMemoryProvider,INeutralOriginalGuestMapProvider,INeutralOriginalGuestUnmapProvider,
        INeutralOriginalVirtualIoProvider,INeutralOriginalVirtualIoCloseProvider,INeutralOriginalVirtualIoBindCleanupProvider,
        INeutralOriginalVirtualIoTransferProvider,INeutralOriginalVirtualIoSnapshotReleaseProvider,
        INeutralOriginalVirtualIoSnapshotReleaseContinuationProvider { }
    public class NeutralReleaseStage : DispatchProxy
    {
        public HybridCpuExecutableChildAdapter Inner=null!;
        public Action<MethodInfo>? After;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            object? raw=method!.Invoke(Inner,args);
            After?.Invoke(method);return raw;
        }
    }
    public class StageProvider : DispatchProxy
    {
        public IPlatformAuthorityProvider Inner=null!;
        public Action<MethodInfo,object?[]?>? After;
        public Func<PlatformProviderIncarnation,PlatformProviderIncarnation>? Observe;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            object? raw =method!.Invoke(Inner,args);
            After?.Invoke(method,args);
            return method.Name=="get_CurrentIncarnation" && Observe is not null ? Observe((PlatformProviderIncarnation)raw!) : raw;
        }
    }
    public class CloseSpy : ExactVirtualIoBackingConsumerTests.BackingSpy
    {
        public bool BeforeEffect;
        public bool BeforeBind;
        public bool ReleaseReplyLoss,ReleaseQueryLoss,ReleaseCopy,ReleaseBeforeEffect;
        public ExternalOriginalVirtualIoSnapshotReleaseRequest? ReleaseRequest;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(method!.Name=="ResolveOriginalVirtualIoSnapshotRelease" && ReleaseQueryLoss)throw new IOException("Snapshot release query lost.");
            if(method.Name=="ReleaseOriginalVirtualIoSnapshot")
            {
                ReleaseRequest=(ExternalOriginalVirtualIoSnapshotReleaseRequest)args![0]!;
                if(ReleaseBeforeEffect)throw new IOException("Snapshot release delivery lost before native admission.");
            }
            if(BeforeEffect && method!.Name=="CloseOriginalVirtualIo")
                throw new IOException("Original Close delivery lost before provider admission.");
            if(BeforeBind && method!.Name=="BindOriginalParentDevice")
                throw new IOException("Original Bind delivery lost before provider admission.");
            object? raw =base.Invoke(method,args);
            if(method.Name=="ReleaseOriginalVirtualIoSnapshot")
            {
                if(ReleaseReplyLoss)throw new IOException("Snapshot release reply lost after effect.");
                if(ReleaseCopy)return Clone(raw!);
            }
            return raw;
        }
    }

    [Theory]
    [InlineData("io-close-lost")] [InlineData("io-close-lost-query-recover")]
    [InlineData("device-close-lost-query-recover")] [InlineData("io-close-copy")]
    [InlineData("before-effect")] [InlineData("publication-loss")]
    [InlineData("owner-copy")] [InlineData("route-loss")] [InlineData("route-copy")]
    [InlineData("charge-copy")] [InlineData("source-revision")] [InlineData("local-refusal")]
    [InlineData("transfer-charge-copy")]
    [InlineData("transfer-session-flag")] [InlineData("transfer-session-flag-foreign")]
    [InlineData("transfer-retired-partial")] [InlineData("transfer-retired-copy")] [InlineData("transfer-retired-loss")]
    [InlineData("kernel-completed-flag")]
    [InlineData("transfer-completed-flag")] [InlineData("transfer-completed-flag-foreign")]
    [InlineData("kernel-completion-copy")] [InlineData("kernel-completion-loss")] [InlineData("kernel-completion-flag-loss")]
    [InlineData("transfer-completion-copy")] [InlineData("transfer-completion-loss")] [InlineData("transfer-completion-flag-loss")]
    [InlineData("kernel-own-noeffect-flag")] [InlineData("kernel-own-noeffect-unknown")]
    public async Task OriginalCloseInvocationRetainsCompositeBackingAndIndependentMemory(string mode)
    {
        bool kernelCompleted=mode.StartsWith("kernel-complet",StringComparison.Ordinal) || mode=="kernel-own-noeffect-flag";
        HybridCpuExternalRuntime native =new HybridCpuExternalRuntime();
        ExactVirtualIoBackingConsumerTests.IOriginalBackingRuntime external =DispatchProxy.Create<ExactVirtualIoBackingConsumerTests.IOriginalBackingRuntime,CloseSpy>();
        CloseSpy spy =(CloseSpy)(object)external;spy.Inner=native;
        RuntimeKernel kernel =new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(),new HybridCpuExecutableChildAdapter(native,external)));
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel,3041,30410); ProcessHandle service =TestFixtures.Create(kernel,3042,30420).Handle;
        ProcessHandle admin =TestFixtures.Create(kernel,3043,30430).Handle;
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(service,32).Value!;foreign.Span[0]=0x7A;
        CapabilityId administration =kernel.MintCapability(new(30430),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(admin,administration,owner,"session-close-io",[new(ServiceBudgetDimension.OwnedMemoryBytes,mode.StartsWith("transfer-",StringComparison.Ordinal) ? 1026UL : 1024UL)]).Value!.ProcessBudget;
        OwnedBuffer<byte> region =kernel.AllocateBuffer<byte>(owner,512).Value!;
        CapabilityId create =kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain =kernel.CreateVirtualDomain(owner,create,new(1,4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess);
        IDictionary roots =(IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains",Private)!.GetValue(kernel.PlatformAuthority)!;
        object rootOwner =Assert.Single(roots.Values.Cast<object>());
        PlatformDomainBinding root =(PlatformDomainBinding)rootOwner.GetType().GetProperty("Binding")!.GetValue(rootOwner)!;
        CapabilityId source =kernel.MintCapability(process.DomainId,owner,ResourceKind.Device,"device:session-close-io",CapabilityRights.Read|CapabilityRights.Write|CapabilityRights.Configure).Value!.CapabilityId;
        PlatformDeviceLease device =kernel.BindPlatformDevice(owner,root,source,PlatformDeviceRights.Read|PlatformDeviceRights.Write).Value!;
        KernelResult<VirtualIoBinding> io =kernel.BindVirtualIo(owner,domain.Domain,device,new(PlatformDeviceRights.Read|PlatformDeviceRights.Write,512));Assert.True(io.IsSuccess,io.Message);
        object local =Assert.Single(((IDictionary)kernel.GetType().GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>());
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"session-close-io",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(owner,descriptor,[]).Value;
        RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        IVirtualizationService client =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        if(mode.StartsWith("transfer-",StringComparison.Ordinal))
        {
            // This actual provider lacks independent snapshot release: whole-IO
            // closure must consume the read charge before retiring delivered custody.
            ValueTask<VirtualIoTransferResponse> reading =client.TransferIoAsync(new(io.Value.Id,io.Value.Generation,0,false,2,default));
            Assert.True(host.ProcessNext().IsSuccess);Assert.True((await reading).Completed);
            object invocations =kernel.GetType().GetField("_endpointSessionInvocationRegistry",Private)!.GetValue(kernel)!;
            IDictionary retained =(IDictionary)invocations.GetType().GetField("_ownedRecords",Private)!.GetValue(invocations)!;
            object invocation =Assert.Single(retained.Values.Cast<object>());
            object transfer =((IList)local.GetType().GetProperty("Transfers",Private)!.GetValue(local)!).Cast<object>().Single();
            object exactOwner=invocation.GetType().GetProperty("OriginalVirtualIoTransfer")!.GetValue(invocation)!;
            FieldInfo chargeField =transfer.GetType().GetField("ChargeOwner",Private)!;
            object? originalCharge =chargeField.GetValue(transfer);
            bool retired=mode.StartsWith("transfer-retired-",StringComparison.Ordinal) || mode.StartsWith("transfer-completion-",StringComparison.Ordinal);
            if(retired)
            {
                PropertyInfo backing=local.GetType().GetProperty("BackingChargeOwner",Private)!;
                object originalBacking=backing.GetValue(local)!;backing.SetValue(local,Clone(originalBacking));
                Assert.False(kernel.CloseVirtualIo(io.Value).IsSuccess);
                Assert.Empty(retained);Assert.Equal(1024UL,Used()); // Snapshot settled, backing remains charged.
                transfer.GetType().GetField("Consumed",Private)!.SetValue(transfer,false);
                transfer.GetType().GetField("SessionClosureConsumed",Private)!.SetValue(transfer,false);
                FieldInfo captured=transfer.GetType().GetField("SessionOriginal",Private)!;
                object exact=captured.GetValue(transfer)!;
                if(mode.StartsWith("transfer-retired-",StringComparison.Ordinal) && mode!="transfer-retired-partial")
                {
                    captured.SetValue(transfer,mode.EndsWith("loss",StringComparison.Ordinal)?null:Clone(exact));
                    Assert.False(kernel.CloseVirtualIo(io.Value).IsSuccess);Assert.Equal(1024UL,Used());Assert.Empty(retained);
                    captured.SetValue(transfer,exact);
                }
                if(mode is "transfer-completion-copy" or "transfer-completion-loss")
                {
                    FieldInfo completion=transfer.GetType().GetField("Completion",Private)!;
                    object originalCompletion=completion.GetValue(transfer)!;
                    completion.SetValue(transfer,mode.EndsWith("loss",StringComparison.Ordinal)?null:Clone(originalCompletion));
                    Assert.False(kernel.CloseVirtualIo(io.Value).IsSuccess);Assert.Equal(1024UL,Used());Assert.Empty(retained);
                    Assert.Same(originalCompletion,transfer.GetType().GetField("OriginalCompletion",Private)!.GetValue(transfer));
                    completion.SetValue(transfer,originalCompletion);
                }
                backing.SetValue(local,originalBacking);
            }
            if(mode=="transfer-charge-copy")chargeField.SetValue(transfer,Clone(originalCharge!));
            else if(!retired)transfer.GetType().GetField(mode.StartsWith("transfer-completed-",StringComparison.Ordinal)?"Consumed":"SessionClosureConsumed",Private)!.SetValue(transfer,true);
            if(mode.EndsWith("foreign",StringComparison.Ordinal))invocation.GetType().GetProperty("OriginalVirtualIoTransfer")!.SetValue(invocation,Clone(exactOwner));
            if(!retired && mode is not ("transfer-session-flag" or "transfer-completed-flag"))
            {
                Assert.False(kernel.CloseVirtualIo(io.Value).IsSuccess);
                Assert.Same(invocation,Assert.Single(retained.Values.Cast<object>()));
                Assert.False((bool)invocation.GetType().GetProperty("VirtualIoTransferContained")!.GetValue(invocation)!);
                Assert.Equal(1026UL,Used());Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
                chargeField.SetValue(transfer,originalCharge);
                invocation.GetType().GetProperty("OriginalVirtualIoTransfer")!.SetValue(invocation,exactOwner);
            }
            Assert.True(kernel.CloseVirtualIo(io.Value).IsSuccess);
            Assert.Empty(retained);Assert.Equal(512UL,Used());
            Assert.Equal(1,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
            Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0UL,Used());
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            Assert.Equal(0x7A,foreign.Span[0]);Assert.True(kernel.Processes.Resolve(service).IsSuccess);
            return;
        }
        if(mode=="kernel-completed-flag")local.GetType().GetProperty("ClosureConsumed",Private)!.SetValue(local,true);
        object? nativeBackingOwner=null;
        if(mode.StartsWith("kernel-own-noeffect-",StringComparison.Ordinal))
        {
            object child=Assert.Single(((IDictionary)native.GetType().GetField("childRegistry",Private)!.GetValue(native)!).Values.Cast<object>());
            object originalIo=Assert.Single(((IEnumerable)child.GetType().GetProperty("OriginalVirtualIo")!.GetValue(child)!).Cast<object>());
            nativeBackingOwner=originalIo.GetType().GetProperty("Device")!.GetValue(originalIo)!;
            Assert.Equal(512,((byte[])nativeBackingOwner.GetType().GetField("Backing")!.GetValue(nativeBackingOwner)!).Length);
            local.GetType().GetProperty("OwnNoEffect",Private)!.SetValue(local,true);
        }
        Task pending =client.CloseIoAsync(new(io.Value.Id,io.Value.Generation+(mode=="local-refusal" ? 1UL : 0))).AsTask();
        object registry =kernel.GetType().GetField("_endpointSessionInvocationRegistry",Private)!.GetValue(kernel)!;
        IDictionary owned =(IDictionary)registry.GetType().GetField("_ownedRecords",Private)!.GetValue(registry)!;
        IDictionary routes =(IDictionary)registry.GetType().GetField("_records",Private)!.GetValue(registry)!;
        object fact =Assert.Single(owned.Values.Cast<object>());
        object? routeKey=null;object? routed=null;object? charge=null;
        spy.Fault=mode;spy.BeforeEffect=mode=="before-effect";
        if(mode=="kernel-own-noeffect-unknown")spy.Fault="io-close-copy";
        kernel.BeforeVirtualIoClosureLocalCommit=()=>
        {
            kernel.BeforeVirtualIoClosureLocalCommit=null;
            Assert.Equal(KernelError.InvalidTransition,host.ProcessNext().Error);
            object invocation =Assert.Single(owned.Values.Cast<object>());
            Assert.Same(local,invocation.GetType().GetProperty("OriginalVirtualIoClose")!.GetValue(invocation));
            if(mode=="owner-copy")invocation.GetType().GetProperty("OriginalVirtualIoClose")!.SetValue(invocation,Clone(local));
            if(mode is "route-loss" or "route-copy")
            {
                routeKey=Assert.Single(routes.Keys.Cast<object>());routed=routes[routeKey];
                if(mode=="route-loss")routes.Remove(routeKey);else routes[routeKey]=Clone(routed!);
            }
            if(mode=="charge-copy")
            {
                PropertyInfo property =local.GetType().GetProperty("BackingChargeOwner",Private)!;charge=property.GetValue(local);property.SetValue(local,Clone(charge!));
            }
            if(mode=="source-revision")
            {
                PropertyInfo state =local.GetType().GetProperty("State",Private)!;state.SetValue(local,state.GetValue(local));
            }
        };
        if(mode=="publication-loss")kernel.SessionInvocationSettlementReservedHook=()=>
        {
            kernel.SessionInvocationSettlementReservedHook=null;
            throw new IOException("Close response lost after composite closure.");
        };
        if(mode=="publication-loss")Assert.Throws<IOException>(()=>host.ProcessNext());
        else Assert.Equal(mode=="io-close-lost" || kernelCompleted,host.ProcessNext().IsSuccess);
        if(mode=="kernel-own-noeffect-flag")
        {
            object? backing=nativeBackingOwner!.GetType().GetField("Backing")!.GetValue(nativeBackingOwner);
            Assert.True(spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo")==1 && backing is null,
                $"Close{spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo")}/backing{(backing as byte[])?.Length.ToString() ?? "closed"}/charge{Used()}");
        }
        if(mode=="io-close-lost" || kernelCompleted){await pending;Assert.Empty(owned);}
        else if(mode=="local-refusal"){await Assert.ThrowsAnyAsync<Exception>(async()=>await pending);Assert.Empty(owned);}
        else Assert.Same(fact,Assert.Single(owned.Values.Cast<object>()));
        if(mode=="local-refusal")Assert.False((bool)fact.GetType().GetProperty("ServiceAccepted")!.GetValue(fact)!);
        else Assert.NotNull(fact.GetType().GetProperty("OriginalVirtualIoClose")!.GetValue(fact));
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        bool partial =mode is "owner-copy" or "route-loss" or "route-copy";
        bool completed =mode is "io-close-lost" or "publication-loss" || kernelCompleted;
        Assert.Equal(partial || completed ? 512UL : 1024UL,Used());
        if(mode is "kernel-completion-copy" or "kernel-completion-loss")
        {
            PropertyInfo completion=local.GetType().GetProperty("Completion",Private)!;
            object originalCompletion=completion.GetValue(local)!;
            completion.SetValue(local,mode.EndsWith("loss",StringComparison.Ordinal)?null:Clone(originalCompletion));
            Assert.False(kernel.CloseVirtualIo(io.Value).IsSuccess);Assert.Equal(512UL,Used());
            Assert.Same(originalCompletion,local.GetType().GetProperty("OriginalCompletion",Private)!.GetValue(local));
            Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
            completion.SetValue(local,originalCompletion);
        }
        if(mode=="kernel-completion-flag-loss")
        {
            local.GetType().GetProperty("ClosureConsumed",Private)!.SetValue(local,false);
            PropertyInfo state=local.GetType().GetProperty("State",Private)!;
            state.SetValue(local,Enum.Parse(state.PropertyType,"Active"));
            Assert.False(kernel.TransferVirtualIo(owner,new(io.Value,0,true,1,[0x55])).IsSuccess);
            Assert.Equal(0,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
            Assert.True(kernel.CloseVirtualIo(io.Value).IsSuccess);Assert.Equal(512UL,Used());
        }
        if(routeKey is not null)routes[routeKey]=routed;
        if(mode=="owner-copy")fact.GetType().GetProperty("OriginalVirtualIoClose")!.SetValue(fact,local);
        if(mode=="charge-copy")local.GetType().GetProperty("BackingChargeOwner",Private)!.SetValue(local,charge);
        kernel.BeforeVirtualIoClosureLocalCommit=null;
        spy.Fault="exact";spy.BeforeEffect=false;
        _=kernel.RevokeCapability(source); // Permission revocation and cascade closure are independent results.
        Assert.False(kernel.ValidateCapability(owner,source,CapabilityRights.Read).IsSuccess);
        if(mode=="io-close-lost" || kernelCompleted)
        {
            ValueTask completedAgain =client.CloseIoAsync(new(io.Value.Id,io.Value.Generation));
            Assert.True(host.ProcessNext().IsSuccess);await completedAgain;
            Assert.Equal(512UL,Used());Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
        }
        if(mode is "before-effect" or "io-close-copy" or "kernel-own-noeffect-unknown")
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1024UL,Used());
        }
        else
        {
            KernelResult closed =kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
            Assert.Equal(0UL,Used());Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        }
        if(mode=="io-close-lost" || kernelCompleted)await pending;else await Assert.ThrowsAnyAsync<Exception>(async()=>await pending);
        Assert.Equal(mode=="before-effect" ? 0 : 1,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
        Assert.Equal(0x7A,foreign.Span[0]);Assert.True(kernel.Processes.Resolve(service).IsSuccess);
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
    }
    [Theory]
    [InlineData("budget")] [InlineData("stale-domain")] [InlineData("source-revoked")]
    [InlineData("bind-delivery-loss")] [InlineData("io-bind-query-throw")]
    [InlineData("transfer-budget")] [InlineData("transfer-input")]
    [InlineData("transfer-rights")] [InlineData("transfer-closed")]
    [InlineData("transfer-stage-prepare")] [InlineData("transfer-stage-source")]
    [InlineData("transfer-stage-owner")] [InlineData("transfer-stage-charge")]
    [InlineData("transfer-stage-refusal-copy")] [InlineData("transfer-stage-refusal-loss")]
    [InlineData("transfer-stage-fence-loss")]
    [InlineData("transfer-stage-fence-copy")] [InlineData("transfer-stage-fence-route-loss")]
    [InlineData("transfer-native-noeffect")]
    [InlineData("transfer-native-noeffect-session-flag")]
    [InlineData("bind-noeffect-copy")] [InlineData("bind-noeffect-loss")]
    [InlineData("bind-noeffect-request-copy")] [InlineData("bind-noeffect-callback-copy")] [InlineData("bind-noeffect-flag-loss")]
    public async Task LocalBindRefusalClosesOnlyItsOwnInvocationWithoutContainingUnknownOriginal(string mode)
    {
        HybridCpuExternalRuntime native =new HybridCpuExternalRuntime();
        ExactVirtualIoBackingConsumerTests.IOriginalBackingRuntime external =DispatchProxy.Create<ExactVirtualIoBackingConsumerTests.IOriginalBackingRuntime,CloseSpy>();
        CloseSpy spy =(CloseSpy)(object)external;spy.Inner=native;
        IPlatformAuthorityProvider provider=new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(),new HybridCpuExecutableChildAdapter(native,external));
        StageProvider? stage=null;
        bool staged =mode.StartsWith("transfer-stage-",StringComparison.Ordinal);
        bool bindNoEffect=mode.StartsWith("bind-noeffect-",StringComparison.Ordinal);
        bool nativeNoEffect =mode.StartsWith("transfer-native-noeffect",StringComparison.Ordinal);
        if(staged || bindNoEffect)
        {
            IStageProvider proxy =DispatchProxy.Create<IStageProvider,StageProvider>();stage=(StageProvider)(object)proxy;stage.Inner=provider;provider=proxy;
        }
        RuntimeKernel kernel =new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel,3051,30510); ProcessHandle service =TestFixtures.Create(kernel,3052,30520).Handle;
        ProcessHandle admin =TestFixtures.Create(kernel,3053,30530).Handle;
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(service,32).Value!;foreign.Span[0]=0x7B;
        CapabilityId administration =kernel.MintCapability(new(30530),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(admin,administration,owner,"local-bind-refusal",[new(ServiceBudgetDimension.OwnedMemoryBytes,mode=="budget" ? 512UL : staged || nativeNoEffect ? 1026UL : 1024UL)]).Value!.ProcessBudget;
        OwnedBuffer<byte> region =kernel.AllocateBuffer<byte>(owner,512).Value!;
        CapabilityId create =kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain =kernel.CreateVirtualDomain(owner,create,new(1,4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess);
        IDictionary roots =(IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains",Private)!.GetValue(kernel.PlatformAuthority)!;
        object rootOwner =Assert.Single(roots.Values.Cast<object>());
        PlatformDomainBinding root =(PlatformDomainBinding)rootOwner.GetType().GetProperty("Binding")!.GetValue(rootOwner)!;
        CapabilityId source =kernel.MintCapability(process.DomainId,owner,ResourceKind.Device,"device:local-bind-refusal",CapabilityRights.Read|CapabilityRights.Write|CapabilityRights.Configure).Value!.CapabilityId;
        PlatformDeviceLease device =kernel.BindPlatformDevice(owner,root,source,PlatformDeviceRights.Read|PlatformDeviceRights.Write).Value!;
        bool transferMode =mode.StartsWith("transfer-",StringComparison.Ordinal);
        VirtualIoBinding io=default;
        if(transferMode)
        {
            KernelResult<VirtualIoBinding> admitted =kernel.BindVirtualIo(owner,domain.Domain,device,new(mode=="transfer-rights" ? PlatformDeviceRights.Read : PlatformDeviceRights.Read|PlatformDeviceRights.Write,512));
            Assert.True(admitted.IsSuccess,admitted.Message);io=admitted.Value;
            if(mode=="transfer-closed")Assert.True(kernel.CloseVirtualIo(io).IsSuccess);
        }
        if(mode.StartsWith("transfer-stage-fence-",StringComparison.Ordinal))
        {
            object local=Assert.Single(((IDictionary)kernel.GetType().GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>());
            PlatformVirtualIoBinding binding=(PlatformVirtualIoBinding)local.GetType().GetProperty("Platform",Private)!.GetValue(local)!;
            var prepared=kernel.PlatformAuthority.PrepareVirtualIoTransfer(binding,0,false,2,null);Assert.True(prepared.IsSuccess);
            int localCalls=0;
            Assert.False(kernel.PlatformAuthority.ConsumeUninvokedVirtualIoTransfer(binding,prepared.Value!,()=>{localCalls++;return KernelResult.Fail(KernelError.PlatformFaulted,"Local settlement retry required.");}).IsSuccess);
            object bridgeIo=Assert.Single(((IDictionary)typeof(PlatformAuthorityBridge).GetField("_virtualIoBindings",Private)!.GetValue(kernel.PlatformAuthority)!).Values.Cast<object>());
            object bridgeTransfer=Assert.Single(((IList)bridgeIo.GetType().GetProperty("Transfers")!.GetValue(bridgeIo)!).Cast<object>());
            bridgeTransfer.GetType().GetField("NeverInvokedRetired")!.SetValue(bridgeTransfer,false);
            Assert.False(kernel.PlatformAuthority.InvokeVirtualIoTransfer(binding,prepared.Value!).IsSuccess);
            Assert.Equal(0,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));Assert.Equal(1,localCalls);
            FieldInfo fence=bridgeTransfer.GetType().GetField("Fence")!;
            object exact=fence.GetValue(bridgeTransfer)!;
            if(mode!="transfer-stage-fence-loss")
            {
                fence.SetValue(bridgeTransfer,mode.EndsWith("copy",StringComparison.Ordinal)?Clone(exact):null);
                Assert.False(kernel.PlatformAuthority.InvokeVirtualIoTransfer(binding,prepared.Value!).IsSuccess);
                Assert.False(kernel.PlatformAuthority.ConsumeUninvokedVirtualIoTransfer(binding,prepared.Value!,()=>{localCalls++;return KernelResult.Ok();}).IsSuccess);
                Assert.Equal(1,localCalls);Assert.Equal(1024UL,Used());Assert.Equal(0x7B,foreign.Span[0]);
                Assert.Same(exact,bridgeTransfer.GetType().GetField("OriginalFence")!.GetValue(bridgeTransfer));
                fence.SetValue(bridgeTransfer,exact);
            }
            Assert.True(kernel.PlatformAuthority.ConsumeUninvokedVirtualIoTransfer(binding,prepared.Value!,()=>{localCalls++;return KernelResult.Ok();}).IsSuccess);
            Assert.Equal(2,localCalls);Assert.Equal(0,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0UL,Used());Assert.Equal(0x7B,foreign.Span[0]);return;
        }
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"local-bind-refusal",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(owner,descriptor,[]).Value;
        RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        IVirtualizationService client =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        object? stagedFact=null;object? originalCharge=null;object? originalInvocation=null;object? invocationRecord=null;
        object? originalBindRequest=null;
        if(staged && stage is not null)stage.Observe=incarnation=>
        {
            object[] locals =((IDictionary)kernel.GetType().GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>().ToArray();
            if(locals.Length!=1)return incarnation;
            object[] facts =((IList)locals[0].GetType().GetProperty("Transfers",Private)!.GetValue(locals[0])!).Cast<object>().ToArray();
            if(facts.Length!=1)return incarnation;
            object candidate =facts[0];
            if(mode is "transfer-stage-prepare" or "transfer-stage-refusal-copy" or "transfer-stage-refusal-loss")
            {
                if(mode!="transfer-stage-prepare" && stagedFact is null)
                {
                    stagedFact=candidate;FieldInfo charge=candidate.GetType().GetField("ChargeOwner",Private)!;
                    originalCharge=charge.GetValue(candidate);charge.SetValue(candidate,Clone(originalCharge!));
                }
                return new(0);
            }
            if(candidate.GetType().GetField("Platform",Private)!.GetValue(candidate) is null || stagedFact is not null)return incarnation;
            stagedFact=candidate;
            object registry =kernel.GetType().GetField("_endpointSessionInvocationRegistry",Private)!.GetValue(kernel)!;
            invocationRecord=Assert.Single(((IDictionary)registry.GetType().GetField("_ownedRecords",Private)!.GetValue(registry)!).Values.Cast<object>());
            if(mode=="transfer-stage-owner")
            {
                PropertyInfo property =invocationRecord.GetType().GetProperty("OriginalVirtualIoTransfer")!;
                originalInvocation=property.GetValue(invocationRecord);property.SetValue(invocationRecord,Clone(originalInvocation!));
            }
            if(mode=="transfer-stage-charge")
            {
                FieldInfo field =candidate.GetType().GetField("ChargeOwner",Private)!;originalCharge=field.GetValue(candidate);field.SetValue(candidate,Clone(originalCharge!));
            }
            return new(incarnation.Value+1);
        };
        if(bindNoEffect)stage!.Observe=incarnation=>
        {
            object? candidate=((IDictionary)kernel.GetType().GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>().SingleOrDefault();
            if(candidate is null || stagedFact is not null || candidate.GetType().GetProperty("OriginalBindRequest",Private)!.GetValue(candidate) is not { } request)return incarnation;
            stagedFact=candidate;originalBindRequest=request;
            Assert.False(Monitor.IsEntered(kernel.GetType().GetField("_virtualIoGate",Private)!.GetValue(kernel)!));
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate",Private)!.GetValue(kernel.PlatformAuthority)!));
            Assert.Equal(KernelError.InvalidTransition,host.ProcessNext().Error);
            Assert.Equal(0,spy.Calls.GetValueOrDefault("BindOriginalVirtualIo"));
            PropertyInfo charge=candidate.GetType().GetProperty("BackingChargeOwner",Private)!;
            originalCharge=charge.GetValue(candidate);charge.SetValue(candidate,Clone(originalCharge!));
            if(mode=="bind-noeffect-callback-copy")candidate.GetType().GetProperty("OriginalBindRequest",Private)!.SetValue(candidate,Clone(request));
            throw new IOException("Original source observation refused before native Bind.");
        };
        if(mode=="source-revoked")_=kernel.RevokeCapability(source);
        spy.BeforeBind=mode=="bind-delivery-loss";spy.Fault=mode;
        BindVirtualIoRequest request =new BindVirtualIoRequest(mode=="stale-domain" ? domain.Domain with {Generation=new(domain.Domain.Generation.Value+1)} : domain.Domain,
            device.LeaseId.Value,device.Generation.Value,VirtualIoAccess.Read|VirtualIoAccess.Write,512);
        Task<VirtualIoTransferResponse>? transferReply =transferMode ? client.TransferIoAsync(new(io.Id,io.Generation,nativeNoEffect ? 512UL : 0,mode is "transfer-input" or "transfer-rights",2,
            mode=="transfer-rights" ? new SingPlus.Sip.Native.BoundedBytes(new byte[]{1,2}) : default)).AsTask() : null;
        Task reply=transferReply is not null ? transferReply : client.BindIoAsync(request).AsTask();
        object registry =kernel.GetType().GetField("_endpointSessionInvocationRegistry",Private)!.GetValue(kernel)!;
        IDictionary owned =(IDictionary)registry.GetType().GetField("_ownedRecords",Private)!.GetValue(registry)!;
        object fact =Assert.Single(owned.Values.Cast<object>());
        if(mode=="transfer-native-noeffect-session-flag")spy.After=name=>
        {
            if(name!="TransferOriginalVirtualIo")return;
            Assert.False(Monitor.IsEntered(kernel.GetType().GetField("_virtualIoGate",Private)!.GetValue(kernel)!));
            Assert.False(kernel.CloseVirtualIo(io).IsSuccess);
            object original=fact.GetType().GetProperty("OriginalVirtualIoTransfer")!.GetValue(fact)!;
            original.GetType().GetField("SessionClosureConsumed",Private)!.SetValue(original,true);
        };
        Assert.Equal(nativeNoEffect,host.ProcessNext().IsSuccess);
        spy.After=null;
        if(nativeNoEffect)Assert.True((bool)fact.GetType().GetProperty("VirtualIoTransferNoEffect")!.GetValue(fact)!);
        if(nativeNoEffect){ VirtualIoTransferResponse result =await transferReply!;Assert.True(result.NoEffect);Assert.False(result.Completed);}
        else await Assert.ThrowsAnyAsync<Exception>(async()=>await reply);

        if(bindNoEffect)
        {
            Assert.NotNull(stagedFact);Assert.Equal(1024UL,Used());stage!.Observe=null;
            PropertyInfo noEffect=stagedFact!.GetType().GetProperty("NoEffect",Private)!;
            object exact=noEffect.GetValue(stagedFact)!;
            Assert.Same(exact,stagedFact.GetType().GetProperty("OriginalNoEffect",Private)!.GetValue(stagedFact));
            Assert.Same(originalBindRequest,exact.GetType().GetProperty("Request",Private)!.GetValue(exact));
            stagedFact.GetType().GetProperty("BackingChargeOwner",Private)!.SetValue(stagedFact,originalCharge);
            PropertyInfo requestSlot=stagedFact.GetType().GetProperty("OriginalBindRequest",Private)!;
            if(mode=="bind-noeffect-copy")noEffect.SetValue(stagedFact,Clone(exact));
            if(mode=="bind-noeffect-loss")noEffect.SetValue(stagedFact,null);
            if(mode=="bind-noeffect-request-copy")requestSlot.SetValue(stagedFact,Clone(originalBindRequest!));
            if(mode=="bind-noeffect-flag-loss")stagedFact.GetType().GetProperty("OwnNoEffect",Private)!.SetValue(stagedFact,false);
            else
            {
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(1024UL,Used());
                Assert.False((bool)fact.GetType().GetProperty("VirtualIoBindContained")!.GetValue(fact)!);
                Assert.Same(exact,stagedFact.GetType().GetProperty("OriginalNoEffect",Private)!.GetValue(stagedFact));
                Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            }
            _=kernel.RevokeCapability(source);
            Assert.False(kernel.ValidateCapability(owner,source,CapabilityRights.Read).IsSuccess);
            noEffect.SetValue(stagedFact,exact);requestSlot.SetValue(stagedFact,originalBindRequest);
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0UL,Used());Assert.Empty(owned);
            Assert.Equal(0,spy.Calls.GetValueOrDefault("BindOriginalVirtualIo"));Assert.Equal(0,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            Assert.Equal(0x7B,foreign.Span[0]);Assert.True(kernel.Processes.Resolve(service).IsSuccess);return;
        }

        bool unknown =mode is "bind-delivery-loss" or "io-bind-query-throw";
        Assert.Equal(mode!="source-revoked",(bool)fact.GetType().GetProperty("ServiceAccepted")!.GetValue(fact)!);
        Assert.Equal(!unknown && !staged && !nativeNoEffect,(bool)fact.GetType().GetProperty(transferMode ? "VirtualIoTransferNeverInvoked" : "VirtualIoBindNeverInvoked")!.GetValue(fact)!);
        Assert.Equal(unknown || staged || nativeNoEffect,fact.GetType().GetProperty(transferMode ? "OriginalVirtualIoTransfer" : "OriginalVirtualIoBind")!.GetValue(fact) is not null);
        if((bool)fact.GetType().GetProperty(transferMode ? "VirtualIoTransferNoEffect" : "VirtualIoBindNeverInvoked")!.GetValue(fact)! ||
            transferMode && (bool)fact.GetType().GetProperty("VirtualIoTransferNeverInvoked")!.GetValue(fact)!)Assert.Empty(owned);
        bool refusalCustody=mode.StartsWith("transfer-stage-refusal-",StringComparison.Ordinal);
        Assert.Equal(mode=="transfer-stage-charge" || refusalCustody ? 1026UL : unknown || transferMode && mode!="transfer-closed" ? 1024UL : 512UL,Used());
        if(stage is not null || nativeNoEffect)
        {
            if(stage is not null)stage.Observe=null;
            if(mode=="transfer-stage-owner")invocationRecord!.GetType().GetProperty("OriginalVirtualIoTransfer")!.SetValue(invocationRecord,originalInvocation);
            if(mode=="transfer-stage-charge")stagedFact!.GetType().GetField("ChargeOwner",Private)!.SetValue(stagedFact,originalCharge);
            stagedFact ??=fact.GetType().GetProperty("OriginalVirtualIoTransfer")!.GetValue(fact);
            KernelVirtualIoTransferRequest originalRequest =(KernelVirtualIoTransferRequest)stagedFact!.GetType().GetField("Request",Private)!.GetValue(stagedFact)!;
            if(refusalCustody)
            {
                FieldInfo slot=stagedFact.GetType().GetField("PrepareRefusal",Private)!;
                object exact=slot.GetValue(stagedFact)!;
                stagedFact.GetType().GetField("ChargeOwner",Private)!.SetValue(stagedFact,originalCharge);
                slot.SetValue(stagedFact,mode.EndsWith("loss",StringComparison.Ordinal)?null:Clone(exact));
                Assert.False(kernel.TransferVirtualIo(owner,originalRequest).IsSuccess);Assert.Equal(1026UL,Used());
                Assert.False((bool)fact.GetType().GetProperty("VirtualIoTransferNoEffect")!.GetValue(fact)!);
                Assert.Same(exact,stagedFact.GetType().GetField("OriginalPrepareRefusal",Private)!.GetValue(stagedFact));
                slot.SetValue(stagedFact,exact);
            }
            Assert.Equal(nativeNoEffect,kernel.TransferVirtualIo(owner,originalRequest).IsSuccess); // Cleanup-only, no new transfer.
            Assert.Equal(1024UL,Used());
            Assert.True((bool)fact.GetType().GetProperty("VirtualIoTransferNoEffect")!.GetValue(fact)!);
            Assert.Equal(0,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
            if(stagedFact.GetType().GetField("Platform",Private)!.GetValue(stagedFact) is PlatformOriginalVirtualIoTransferRequest platform)
            {
                object local =Assert.Single(((IDictionary)kernel.GetType().GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>());
                PlatformVirtualIoBinding binding =(PlatformVirtualIoBinding)local.GetType().GetProperty("Platform",Private)!.GetValue(local)!;
                Assert.False(kernel.PlatformAuthority.InvokeVirtualIoTransfer(binding,platform).IsSuccess);
                bool called =false;
                Assert.False(kernel.PlatformAuthority.ConsumeUninvokedVirtualIoTransfer(binding,(PlatformOriginalVirtualIoTransferRequest)Clone(platform),()=>{called=true;return KernelResult.Ok();}).IsSuccess);
                Assert.False(called);
                if(mode=="transfer-stage-fence-loss")
                {
                    object bridgeIo=Assert.Single(((IDictionary)typeof(PlatformAuthorityBridge).GetField("_virtualIoBindings",Private)!.GetValue(kernel.PlatformAuthority)!).Values.Cast<object>());
                    object bridgeTransfer=Assert.Single(((IList)bridgeIo.GetType().GetProperty("Transfers")!.GetValue(bridgeIo)!).Cast<object>());
                    bridgeTransfer.GetType().GetField("NeverInvokedRetired")!.SetValue(bridgeTransfer,false);
                    Assert.False(kernel.PlatformAuthority.InvokeVirtualIoTransfer(binding,platform).IsSuccess);
                    Assert.Equal(0,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
                    Assert.Equal(1024UL,Used());Assert.Equal(0x7B,foreign.Span[0]);
                }
            }
        }
        Assert.Equal(mode=="io-bind-query-throw" || transferMode ? 1 : 0,spy.Calls.GetValueOrDefault("BindOriginalVirtualIo"));
        Assert.Equal(nativeNoEffect ? 1 : 0,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
        if(unknown)
        {
            // A different request cannot re-reserve or bypass the unresolved owner.
            ValueTask<VirtualIoHandleResponse> again =client.BindIoAsync(request);Assert.False(host.ProcessNext().IsSuccess);
            await Assert.ThrowsAnyAsync<Exception>(async()=>await again);
            Assert.Equal(1024UL,Used());
            Assert.DoesNotContain(owned.Values.Cast<object>(),f=>(bool)f.GetType().GetProperty("VirtualIoBindNeverInvoked")!.GetValue(f)!);
            if(mode=="io-bind-query-throw")spy.Fault="exact";
            spy.BeforeBind=false;
        }
        KernelResult closed =kernel.TerminateProcess(owner);
        if(mode=="bind-delivery-loss")
        {
            Assert.False(closed.IsSuccess);Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(1024UL,Used());
        }
        else {Assert.True(closed.IsSuccess,closed.Message);Assert.Equal(0UL,Used());Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);}
        Assert.Equal(mode=="io-bind-query-throw" || transferMode ? 1 : 0,spy.Calls.GetValueOrDefault("BindOriginalVirtualIo"));
        Assert.Equal(nativeNoEffect ? 1 : 0,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
        Assert.Equal(0x7B,foreign.Span[0]);Assert.True(kernel.Processes.Resolve(service).IsSuccess);
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
    }

    [Theory]
    [InlineData("exact")] [InlineData("reply-loss")] [InlineData("query-loss")]
    [InlineData("native-release-request-loss")]
    [InlineData("release-publication-loss")] [InlineData("release-publication-copy")]
    [InlineData("sdk-release-publication-loss")] [InlineData("sdk-release-publication-copy")]
    [InlineData("release-final-publication-loss")] [InlineData("release-final-publication-copy")]
    [InlineData("release-final-sdk-publication-loss")] [InlineData("release-final-sdk-publication-copy")]
    [InlineData("release-final-native-proof-loss")] [InlineData("release-final-native-proof-copy")]
    [InlineData("release-projection-bridge")] [InlineData("release-projection-provider")] [InlineData("release-projection-sdk")]
    [InlineData("copy")] [InlineData("charge-copy")] [InlineData("release-request-loss")] [InlineData("sdk-transfer-raw-loss")] [InlineData("provider-transfer-raw-loss")] [InlineData("bridge-publication-copy")] [InlineData("bridge-publication-history")] [InlineData("bridge-snapshot-history")] [InlineData("bridge-snapshot-copy")] [InlineData("bridge-snapshot-transfer-before")] [InlineData("bridge-snapshot-transfer-after")] [InlineData("release-close-reentry")] [InlineData("release-terminate-reentry")] [InlineData("release-terminate-reentry-query-loss")]
    [InlineData("before-release-effect")] [InlineData("release-source-fresh")] [InlineData("release-source-throw")]
    [InlineData("canceled-read")] [InlineData("canceled-read-release-query")]
    [InlineData("publication-drain")] [InlineData("write-before-close")]
    [InlineData("publication-close")] [InlineData("publication-channel-close")]
    [InlineData("publication-route-loss")] [InlineData("publication-route-service-exit")]
    public async Task CompletedReadReleasesOnlyOriginalNativeSnapshotAfterSessionCopy(string mode)
    {
        IReleaseRuntime external =DispatchProxy.Create<IReleaseRuntime,CloseSpy>(); CloseSpy spy =(CloseSpy)(object)external;spy.Inner=new();
        ExactArtifactReleaseConsumerTests.IOriginalRootRuntime rootProxy =DispatchProxy.Create<ExactArtifactReleaseConsumerTests.IOriginalRootRuntime,ExactArtifactReleaseConsumerTests.RootSpy>();
        ExactArtifactReleaseConsumerTests.RootSpy rootSpy =(ExactArtifactReleaseConsumerTests.RootSpy)(object)rootProxy;rootSpy.Inner=spy.Inner;
        HybridCpuExecutableChildAdapter sdk =new HybridCpuExecutableChildAdapter(rootProxy,external);
        bool sdkReleasePublication=mode.StartsWith("sdk-release-publication-",StringComparison.Ordinal);
        INeutralChildDomainProvider child=sdk;NeutralReleaseStage? neutralStage=null;
        if(sdkReleasePublication)
        {
            INeutralReleaseStage proxy=DispatchProxy.Create<INeutralReleaseStage,NeutralReleaseStage>();
            neutralStage=(NeutralReleaseStage)(object)proxy;neutralStage.Inner=sdk;child=proxy;
        }
        HybridCpuPlatformAuthorityProvider provider =new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(),child);
        IPlatformAuthorityProvider authority=provider;StageProvider? stage=null;
        bool finalSdkReleasePublication=mode.StartsWith("release-final-sdk-publication-",StringComparison.Ordinal);
        bool finalNativeReleaseProof=mode.StartsWith("release-final-native-proof-",StringComparison.Ordinal);
        bool finalReleasePublication=mode.StartsWith("release-final-publication-",StringComparison.Ordinal) || finalSdkReleasePublication || finalNativeReleaseProof;
        bool releasePublication=mode.StartsWith("release-publication-",StringComparison.Ordinal) || sdkReleasePublication || finalReleasePublication;
        if(mode.StartsWith("bridge-",StringComparison.Ordinal) || releasePublication)
        {
            IReadStageProvider proxy =DispatchProxy.Create<IReadStageProvider,StageProvider>();stage=(StageProvider)(object)proxy;stage.Inner=provider;authority=proxy;
        }
        bool routePublication =mode.StartsWith("publication-route-",StringComparison.Ordinal);
        AdmissionClock? admissionClock =routePublication?new AdmissionClock():null;
        RuntimeKernel kernel =new RuntimeKernel(authority,admissionClock);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel,3061,30610); ProcessHandle service =TestFixtures.Create(kernel,3062,30620).Handle;
        ProcessHandle admin =TestFixtures.Create(kernel,3063,30630).Handle;
        ProcessHandle foreignOwner =mode=="publication-route-service-exit"?TestFixtures.Create(kernel,3064,30640).Handle:service;
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(foreignOwner,32).Value!;foreign.Span[0]=0x7C;
        CapabilityId administration =kernel.MintCapability(new(30630),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(admin,administration,owner,"read-snapshot-release",[new(ServiceBudgetDimension.OwnedMemoryBytes,1026)]).Value!.ProcessBudget;
        OwnedBuffer<byte> region =kernel.AllocateBuffer<byte>(owner,512).Value!;
        CapabilityId create =kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain =kernel.CreateVirtualDomain(owner,create,new(1,4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess);
        object rootOwner =Assert.Single(((IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains",Private)!.GetValue(kernel.PlatformAuthority)!).Values.Cast<object>());
        PlatformDomainBinding root =(PlatformDomainBinding)rootOwner.GetType().GetProperty("Binding")!.GetValue(rootOwner)!;
        CapabilityId source =kernel.MintCapability(process.DomainId,owner,ResourceKind.Device,"device:read-release",CapabilityRights.Read|CapabilityRights.Write|CapabilityRights.Configure).Value!.CapabilityId;
        PlatformDeviceLease device =kernel.BindPlatformDevice(owner,root,source,PlatformDeviceRights.Read|PlatformDeviceRights.Write).Value!;
        VirtualIoBinding io =kernel.BindVirtualIo(owner,domain.Domain,device,new(PlatformDeviceRights.Read|PlatformDeviceRights.Write,512)).Value;
        Assert.True(kernel.TransferVirtualIo(owner,new(io,0,true,2,new byte[]{71,73})).IsSuccess);
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"read-release",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(owner,descriptor,[],lifetime:admissionClock is null?null:TimeSpan.FromMinutes(1)).Value;
        RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        IVirtualizationService client =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        bool generatedWrite =mode=="write-before-close";
        if(generatedWrite)
        {
            ValueTask<VirtualIoTransferResponse> writing =client.TransferIoAsync(new(io.Id,io.Generation,2,true,1,new SingPlus.Sip.Native.BoundedBytes(new byte[]{83})));
            Assert.True(host.ProcessNext().IsSuccess);Assert.True((await writing).Completed);
        }
        spy.ReleaseReplyLoss=mode is "reply-loss" or "query-loss" or "release-terminate-reentry-query-loss" or "native-release-request-loss";spy.ReleaseQueryLoss=mode is "query-loss" or "release-terminate-reentry-query-loss" or "native-release-request-loss";spy.ReleaseCopy=mode=="copy";
        spy.ReleaseBeforeEffect=mode=="before-release-effect";
        object? readFact=null;object? originalCharge=null;
        object? releaseOwner=null,originalReleaseRequest=null;
        object? transferRawOwner=null,originalTransferRaw=null;
        object? releaseProjectionOwner=null;FieldInfo? releaseProjection=null;
        bool closeReentryAttempted=false,closeReentryDenied=false,closeReentryRevisionStable=false,closeReentryLocksFree=false;
        bool terminateReentryAttempted=false,terminateReentryDenied=false,terminateReentryLocksFree=false;
        int sourceCallbacks =0; bool sourceLocksFree =false;
        if(mode.StartsWith("release-source-",StringComparison.Ordinal))rootSpy.BeforeQuery=()=>
        {
            object ioOwner =Assert.Single(((IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("originalIo",Private)!.GetValue(sdk)!).Values.Cast<object>());
            object fact =((IEnumerable)ioOwner.GetType().GetProperty("Transfers")!.GetValue(ioOwner)!).Cast<object>().Last();
            FieldInfo field =fact.GetType().GetField("ReleaseRequest")!;
            if(field.GetValue(fact) is not ExternalOriginalVirtualIoSnapshotReleaseRequest original || sourceCallbacks!=0)return;
            sourceCallbacks++;releaseOwner=fact;originalReleaseRequest=original;
            sourceLocksFree=!Monitor.IsEntered(typeof(HybridCpuExecutableChildAdapter).GetField("sync",Private)!.GetValue(sdk)!) &&
                !Monitor.IsEntered(typeof(HybridCpuPlatformAuthorityProvider).GetField("_childCreateCommitGate",Private)!.GetValue(provider)!);
            if(mode=="release-source-throw")rootSpy.ThrowSource=true;
            else field.SetValue(fact,new ExternalOriginalVirtualIoSnapshotReleaseRequest(original.Transfer));
        };
        spy.After=name=>
        {
            if(name!="ReleaseOriginalVirtualIoSnapshot")return;
            if(mode.StartsWith("release-projection-",StringComparison.Ordinal) && releaseProjection is null)
            {
                object holder=mode.EndsWith("bridge",StringComparison.Ordinal)?kernel.PlatformAuthority:
                    mode.EndsWith("sdk",StringComparison.Ordinal)?sdk:provider;
                string registryName=ReferenceEquals(holder,sdk)?"originalIo":ReferenceEquals(holder,provider)?"_originalProviderVirtualIo":"_virtualIoBindings";
                object ioOwner=Assert.Single(((IDictionary)holder.GetType().GetField(registryName,Private)!.GetValue(holder)!).Values.Cast<object>());
                releaseProjectionOwner=((IEnumerable)ioOwner.GetType().GetProperty("Transfers")!.GetValue(ioOwner)!).Cast<object>().Last();
                releaseProjection=releaseProjectionOwner.GetType().GetField(ReferenceEquals(holder,kernel.PlatformAuthority)?"SnapshotReleaseInvoked":"ReleaseInvoked")!;
                Assert.True((bool)releaseProjection.GetValue(releaseProjectionOwner)!);
                releaseProjection.SetValue(releaseProjectionOwner,false);
            }
            Assert.Equal(KernelError.InvalidTransition,host.ProcessNext().Error);
            if(mode.StartsWith("release-terminate-reentry",StringComparison.Ordinal))
            {
                terminateReentryAttempted=true;
                terminateReentryLocksFree=!Monitor.IsEntered(kernel.GetType().GetField("_virtualIoGate",Private)!.GetValue(kernel)!) &&
                    !Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate",Private)!.GetValue(kernel.PlatformAuthority)!);
                terminateReentryDenied=!kernel.TerminateProcess(owner).IsSuccess;
            }
            if(mode=="release-close-reentry")
            {
                object local =Assert.Single(((IDictionary)kernel.GetType().GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>());
                PropertyInfo revision =local.GetType().GetProperty("SourceRevision",Private)!; object? before =revision.GetValue(local);
                closeReentryAttempted=true;
                closeReentryLocksFree=!Monitor.IsEntered(kernel.GetType().GetField("_virtualIoGate",Private)!.GetValue(kernel)!);
                closeReentryDenied=!kernel.CloseVirtualIo(io).IsSuccess;
                closeReentryRevisionStable=Equals(before,revision.GetValue(local));
            }
            if(mode.EndsWith("-transfer-raw-loss",StringComparison.Ordinal))
            {
                object holder=mode.StartsWith("sdk",StringComparison.Ordinal)?sdk:provider;
                IDictionary registry =(IDictionary)holder.GetType().GetField(ReferenceEquals(holder,sdk)?"originalIo":"_originalProviderVirtualIo",Private)!.GetValue(holder)!;
                object ioOwner =Assert.Single(registry.Values.Cast<object>());
                transferRawOwner=((IEnumerable)ioOwner.GetType().GetProperty("Transfers")!.GetValue(ioOwner)!).Cast<object>().Last();
                FieldInfo field =transferRawOwner.GetType().GetField("Raw")!;originalTransferRaw=field.GetValue(transferRawOwner);field.SetValue(transferRawOwner,null);
            }
            if(mode=="release-request-loss")
            {
                object ioOwner =Assert.Single(((IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("originalIo",Private)!.GetValue(sdk)!).Values.Cast<object>());
                releaseOwner=((IEnumerable)ioOwner.GetType().GetProperty("Transfers")!.GetValue(ioOwner)!).Cast<object>().Last();
                FieldInfo requestField =releaseOwner.GetType().GetField("ReleaseRequest")!;
                originalReleaseRequest=requestField.GetValue(releaseOwner);
                requestField.SetValue(releaseOwner,null);
            }
            if(mode=="native-release-request-loss")
            {
                object nativeRoot=Assert.Single(((IDictionary)spy.Inner.GetType().GetField("registry",Private)!.GetValue(spy.Inner)!).Values.Cast<object>());
                object deviceOwner=Assert.Single(((IList)nativeRoot.GetType().GetProperty("OriginalParentDevices")!.GetValue(nativeRoot)!).Cast<object>());
                object nativeIo=Assert.Single(((IList)deviceOwner.GetType().GetProperty("Users")!.GetValue(deviceOwner)!).Cast<object>());
                releaseOwner=((IEnumerable)nativeIo.GetType().GetProperty("Transfers")!.GetValue(nativeIo)!).Cast<object>().Last();
                FieldInfo requestField=releaseOwner.GetType().GetField("ReleaseRequest")!;
                originalReleaseRequest=requestField.GetValue(releaseOwner);Assert.Same(spy.ReleaseRequest,originalReleaseRequest);
                requestField.SetValue(releaseOwner,null);
            }
            if(mode=="charge-copy")
            {
                object local =Assert.Single(((IDictionary)kernel.GetType().GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>());
                readFact=((IList)local.GetType().GetProperty("Transfers",Private)!.GetValue(local)!).Cast<object>().Last();
                FieldInfo field =readFact.GetType().GetField("ChargeOwner",Private)!;originalCharge=field.GetValue(readFact);field.SetValue(readFact,Clone(originalCharge!));
            }
        };

        EndpointSessionInvocationRegistry.OriginalPublication? publishedOriginal=null;
        if(mode=="exact")kernel.SessionInvocationSettlementReservedHook=()=>
        {
            kernel.SessionInvocationSettlementReservedHook=null;
            object pending =host.GetType().GetField("_pendingResult",Private)!.GetValue(host)!;
            publishedOriginal=(EndpointSessionInvocationRegistry.OriginalPublication)pending.GetType().GetField("Publication",Private)!.GetValue(pending)!;
        };
        object? bridgeTransfer=null,bridgeRaw=null;IList? bridgeHistory=null;object[]? bridgeInventory=null;
        int bridgeObserved =0; bool bridgeLocksFree =false; bool queryReturned =false;
        bool bridgeSnapshot =mode.StartsWith("bridge-snapshot-",StringComparison.Ordinal);
        bool bridgePreRelease =mode=="bridge-snapshot-transfer-before";
        string bridgeRawFieldName =bridgeSnapshot && !mode.Contains("-transfer-",StringComparison.Ordinal)?"SnapshotReleaseRaw":"Raw";
        object? publicationOwner=null,originalReleasePublication=null;FieldInfo? publicationSlot=null;
        void LoseReleasePublication(MethodInfo method)
        {
            if(method.Name!="ReleaseOriginalVirtualIoSnapshot" || publicationSlot is not null)return;
            object holder=sdkReleasePublication?sdk:provider;
            object providerIo=Assert.Single(((IDictionary)holder.GetType().GetField(sdkReleasePublication?"originalIo":"_originalProviderVirtualIo",Private)!.GetValue(holder)!).Values.Cast<object>());
            publicationOwner=((IEnumerable)providerIo.GetType().GetProperty("Transfers")!.GetValue(providerIo)!).Cast<object>().Last();
            publicationSlot=publicationOwner.GetType().GetField("ReleasePublished")!;
            originalReleasePublication=publicationSlot.GetValue(publicationOwner);Assert.NotNull(originalReleasePublication);
            publicationSlot.SetValue(publicationOwner,mode.EndsWith("loss",StringComparison.Ordinal)?null:Clone(originalReleasePublication));
            throw new IOException("Original release publication reply lost after provider publication.");
        }
        if(sdkReleasePublication)neutralStage!.After=LoseReleasePublication;
        else if(finalReleasePublication)
        {
            stage!.After=(method,args)=>{if(method.Name=="ResolveOriginalVirtualIoSnapshotRelease")queryReturned=true;};
            stage.Observe=incarnation=>
            {
                if(!queryReturned || publicationSlot is not null)return incarnation;
                Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate",Private)!.GetValue(kernel.PlatformAuthority)!));
                object holder=finalSdkReleasePublication?sdk:provider;
                object providerIo=Assert.Single(((IDictionary)holder.GetType().GetField(finalSdkReleasePublication?"originalIo":"_originalProviderVirtualIo",Private)!.GetValue(holder)!).Values.Cast<object>());
                publicationOwner=((IEnumerable)providerIo.GetType().GetProperty("Transfers")!.GetValue(providerIo)!).Cast<object>().Last();
                publicationSlot=publicationOwner.GetType().GetField("ReleasePublished")!;
                if(finalNativeReleaseProof)
                {
                    object nativeRoot=Assert.Single(((IDictionary)spy.Inner.GetType().GetField("registry",Private)!.GetValue(spy.Inner)!).Values.Cast<object>());
                    object nativeDevice=Assert.Single(((IEnumerable)nativeRoot.GetType().GetProperty("OriginalParentDevices")!.GetValue(nativeRoot)!).Cast<object>());
                    object nativeIo=Assert.Single(((IEnumerable)nativeDevice.GetType().GetProperty("Users")!.GetValue(nativeDevice)!).Cast<object>());
                    publicationOwner=((IEnumerable)nativeIo.GetType().GetProperty("Transfers")!.GetValue(nativeIo)!).Cast<object>().Last();
                    publicationSlot=publicationOwner.GetType().GetField("Release")!;
                }
                originalReleasePublication=publicationSlot.GetValue(publicationOwner);Assert.NotNull(originalReleasePublication);
                publicationSlot.SetValue(publicationOwner,mode.EndsWith("loss",StringComparison.Ordinal)?null:Clone(originalReleasePublication));
                return incarnation;
            };
        }
        else if(releasePublication)stage!.After=(method,args)=>LoseReleasePublication(method);
        if(stage is not null && !releasePublication)
        {
            stage.After=(method,args)=>{if(bridgePreRelease ? method.Name=="get_SupportsOriginalVirtualIoSnapshotRelease" : method.Name==(bridgeSnapshot?"ResolveOriginalVirtualIoSnapshotRelease":"ResolveOriginalVirtualIoTransfer") && args![0] is PlatformOriginalVirtualIoTransferRequest {Write:false})queryReturned=true;};
            stage.Observe=incarnation=>
            {
                if(!queryReturned || bridgeObserved!=0)return incarnation;
                bridgeObserved++;
                bridgeLocksFree=!Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate",Private)!.GetValue(kernel.PlatformAuthority)!);
                object ioOwner =Assert.Single(((IDictionary)typeof(PlatformAuthorityBridge).GetField("_virtualIoBindings",Private)!.GetValue(kernel.PlatformAuthority)!).Values.Cast<object>());
                bridgeHistory=(IList)ioOwner.GetType().GetProperty("Transfers")!.GetValue(ioOwner)!;
                bridgeTransfer=bridgeHistory.Cast<object>().Last();
                FieldInfo field =bridgeTransfer.GetType().GetField(bridgeRawFieldName)!;bridgeRaw=field.GetValue(bridgeTransfer)!;
                if(mode.EndsWith("-copy",StringComparison.Ordinal) || mode.Contains("-transfer-",StringComparison.Ordinal))field.SetValue(bridgeTransfer,Clone(bridgeRaw));
                else {bridgeInventory=bridgeHistory.Cast<object>().ToArray();bridgeHistory.Clear();}
                return incarnation;
            };
        }
        bool canceledRead =mode.StartsWith("canceled-read",StringComparison.Ordinal);
        if(canceledRead)spy.Fault="kernel-read-query-loss";
        ValueTask<VirtualIoTransferResponse> read =client.TransferIoAsync(new(io.Id,io.Generation,0,false,2,default));
        object invocationRegistry =kernel.GetType().GetField("_endpointSessionInvocationRegistry",Private)!.GetValue(kernel)!;
        IDictionary invocationOwners =(IDictionary)invocationRegistry.GetType().GetField("_ownedRecords",Private)!.GetValue(invocationRegistry)!;
        object ReadFact()
        {
            object local =Assert.Single(((IDictionary)kernel.GetType().GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>());
            return ((IList)local.GetType().GetProperty("Transfers",Private)!.GetValue(local)!).Cast<object>().Last();
        }
        if(routePublication)
        {
            IDictionary routes =(IDictionary)invocationRegistry.GetType().GetField("_records",Private)!.GetValue(invocationRegistry)!;
            object key =Assert.Single(routes.Keys.Cast<object>()); object original =routes[key]!; int observed =0;
            admissionClock!.Observe=()=>
            {
                if(host.GetType().GetField("_pendingResult",Private)!.GetValue(host) is null)return;
                admissionClock.Observe=null;observed++;
                routes.Remove(key); // Lifetime observation succeeds; action source is lost afterwards.
            };
            Assert.False(host.ProcessNext().IsSuccess);Assert.Equal(1,observed);Assert.False(read.IsCompleted);
            Assert.Equal(2,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
            Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));Assert.Equal(1024UL,Used());
            Assert.False(host.AdvancePendingResult().IsSuccess);Assert.Equal(1024UL,Used());
            routes[key]=original;
            if(mode=="publication-route-service-exit")
            {
                KernelResult exited =kernel.TerminateProcess(service);Assert.True(exited.IsSuccess,exited.Message);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await read);
                Assert.Equal(1024UL,Used());Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
                Assert.Null(host.GetType().GetField("_pendingResult",Private)!.GetValue(host));
                Assert.Equal(0,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
                Assert.False(host.ProcessNext().IsSuccess);
                Assert.True(kernel.TransferVirtualIo(owner,new(io,0,true,1,[79])).IsSuccess);
                Assert.Equal(3,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
                Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));
                Assert.True(kernel.CloseVirtualIo(io).IsSuccess);Assert.Equal(512UL,Used());
                Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0UL,Used());
                Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
                Assert.Equal(0x7C,foreign.Span[0]);Assert.True(kernel.TerminateProcess(foreignOwner).IsSuccess);
                return;
            }
            KernelResult retry =host.AdvancePendingResult();Assert.True(retry.IsSuccess,retry.Message);
            VirtualIoTransferResponse delivered =await read;Assert.True(delivered.Completed);Assert.Equal(new byte[]{71,73},delivered.Data.ToArray());
            Assert.Empty(invocationOwners);Assert.Equal(1024UL,Used());
            Assert.Equal(2,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));
            Assert.Equal(0,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
            Assert.True(kernel.CloseVirtualIo(io).IsSuccess);Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(0UL,Used());Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            Assert.Equal(new byte[]{71,73},delivered.Data.ToArray());Assert.Equal(0x7C,foreign.Span[0]);
            return;
        }
        if(mode=="publication-drain")
        {
            kernel.SessionInvocationSettlementReservedHook=()=>{kernel.SessionInvocationSettlementReservedHook=null;throw new IOException("Read publication interrupted after snapshot release.");};
            Assert.Throws<IOException>(()=>host.ProcessNext());
            object retained =ReadFact();Assert.NotNull(retained.GetType().GetField("SessionResponse",Private)!.GetValue(retained));
            Assert.Equal(1024UL,Used());Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));
            KernelResult ended =kernel.TerminateProcess(owner);Assert.True(ended.IsSuccess,ended.Message);
            await Assert.ThrowsAnyAsync<Exception>(async()=>await read);
            Assert.Null(retained.GetType().GetField("SessionResponse",Private)!.GetValue(retained));
            Assert.True((bool)retained.GetType().GetField("SessionResponseRetired",Private)!.GetValue(retained)!);
            Assert.Equal(generatedWrite ? 3 : 2,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));Assert.Equal(0UL,Used());Assert.Equal(0x7C,foreign.Span[0]);
            return;
        }
        if(mode is "publication-close" or "publication-channel-close")
        {
            // Hold the existing publication/consumption gate so Close is ordered
            // after exact publication and before the registered waiter's consumption.
            object correlation =kernel.GetType().GetField("_requestResponseCorrelationGate",Private)!.GetValue(kernel)!;
            ResponseRegistry.OriginalPublication? originalCompletion=null;
            lock(correlation)
            {
                Assert.True(host.ProcessNext().IsSuccess);
                object invocation =Assert.Single(invocationOwners.Values.Cast<object>());
                originalCompletion=(ResponseRegistry.OriginalPublication)invocation.GetType().GetProperty("OriginalResponse")!.GetValue(invocation)!;
                if(mode=="publication-close")Assert.True(kernel.CloseSession(owner,session).IsSuccess);
                else Assert.True(kernel.Channels.Close(kernel.ResolveSession(owner,session).Value!.Channel).IsSuccess);
            }
            VirtualIoTransferResponse delivered =await read;
            Assert.True(delivered.Completed);Assert.Equal(new byte[]{71,73},delivered.Data.ToArray());
            ResponseEnvelope originalRaw =originalCompletion.Owner.ReadPublication(originalCompletion).Value!;
            Assert.True(originalCompletion.Owner.ReadConsumedPublication(originalCompletion,originalRaw));
            Assert.Empty(invocationOwners);
            Assert.Equal(1024UL,Used());Assert.Equal(2,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
            Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));
            Assert.Equal(0,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
            Assert.True(kernel.CloseVirtualIo(io).IsSuccess);
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0UL,Used());
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            Assert.Equal(new byte[]{71,73},delivered.Data.ToArray());Assert.Equal(0x7C,foreign.Span[0]);
            return;
        }
        KernelResult first =host.ProcessNext();
        if(mode.StartsWith("release-projection-",StringComparison.Ordinal))
        {
            Assert.NotNull(releaseProjection);
            bool bridgeProjection=mode.EndsWith("bridge",StringComparison.Ordinal);
            Assert.Equal(bridgeProjection,first.IsSuccess);
            if(bridgeProjection)
            {
                VirtualIoTransferResponse response=await read;
                Assert.True(response.Completed);Assert.Equal(new byte[]{71,73},response.Data.ToArray());
            }
            else
            {
                await Assert.ThrowsAnyAsync<Exception>(async()=>await read);
                Assert.Equal(1026UL,Used());Assert.False(host.AdvancePendingResult().IsSuccess);
                Assert.Equal(1026UL,Used());
                releaseProjection!.SetValue(releaseProjectionOwner,true);
                Assert.True(host.AdvancePendingResult().IsSuccess);
            }
            Assert.Equal(1024UL,Used());Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));
            Assert.Equal(2,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0UL,Used());
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));Assert.Equal(0x7C,foreign.Span[0]);return;
        }
        if(releasePublication)
        {
            Assert.False(first.IsSuccess);Assert.NotNull(originalReleasePublication);
            await Assert.ThrowsAnyAsync<Exception>(async()=>await read);
            Assert.Equal(1026UL,Used());Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));
            Assert.Equal(2,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));Assert.Equal(0x7C,foreign.Span[0]);
            stage!.After=null;stage.Observe=null;if(neutralStage is not null)neutralStage.After=null;
            Assert.False(host.AdvancePendingResult().IsSuccess);Assert.Equal(1026UL,Used());
            publicationSlot!.SetValue(publicationOwner,originalReleasePublication);
            Assert.True(host.AdvancePendingResult().IsSuccess);Assert.Equal(1024UL,Used());
            Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0UL,Used());
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);Assert.Equal(0x7C,foreign.Span[0]);return;
        }
        if(mode=="release-close-reentry")
        {
            Assert.True(closeReentryAttempted,"Native Release callback ran");Assert.True(closeReentryDenied,"Reentrant Close denied");Assert.True(closeReentryLocksFree,"Callback outside kernel IO lock");Assert.True(closeReentryRevisionStable,"Denied Close preserves IO source revision");
            Assert.Equal(0,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
        }
        Assert.Equal(mode is "exact" or "reply-loss" or "write-before-close" or "release-close-reentry",first.IsSuccess);
        if(mode.StartsWith("release-terminate-reentry",StringComparison.Ordinal))
        {
            spy.After=null;
            await Assert.ThrowsAnyAsync<Exception>(async()=>await read);
            Assert.True(terminateReentryAttempted);Assert.True(terminateReentryDenied);Assert.True(terminateReentryLocksFree);
            Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));
            Assert.Equal(2,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
            Assert.Equal(mode.EndsWith("query-loss",StringComparison.Ordinal)?1026UL:1024UL,Used());Assert.Equal(0x7C,foreign.Span[0]);
            object retainedResponseFact =ReadFact();
            KernelResult<ProcessTeardownSnapshot> teardown =kernel.ObserveProcessTeardown(owner);Assert.True(teardown.IsSuccess,teardown.Message);
            Assert.True(teardown.Value.LocalAuthorizationRevoked);Assert.True(teardown.Value.ChannelsClosed);Assert.False(teardown.Value.IsComplete);
            Assert.False(kernel.TransferVirtualIo(owner,new(io,0,true,1,[0xEE])).IsSuccess);
            Assert.Equal(2,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
            spy.ReleaseQueryLoss=false;spy.ReleaseReplyLoss=false;
            KernelResult ended =kernel.TerminateProcess(owner);Assert.True(ended.IsSuccess,ended.Message);
            Assert.Equal(0UL,Used());Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));
            Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));Assert.Equal(0x7C,foreign.Span[0]);
            Assert.Null(retainedResponseFact.GetType().GetField("SessionResponse",Private)!.GetValue(retainedResponseFact));
            Assert.True((bool)retainedResponseFact.GetType().GetField("SessionResponseRetired",Private)!.GetValue(retainedResponseFact)!);
            Assert.Empty(invocationOwners);
            Assert.Null(host.GetType().GetField("_pendingOperation",Private)!.GetValue(host));
            Assert.Null(host.GetType().GetField("_pendingResult",Private)!.GetValue(host));return;
        }
        if(mode.StartsWith("release-source-",StringComparison.Ordinal))
        {
            rootSpy.BeforeQuery=null;rootSpy.ThrowSource=false;
            await Assert.ThrowsAnyAsync<Exception>(async()=>await read);
            Assert.Equal(1,sourceCallbacks);Assert.True(sourceLocksFree);
            Assert.Equal(0,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));
            Assert.Equal(1026UL,Used());Assert.Equal(0x7C,foreign.Span[0]);
            NeutralOriginalVirtualIoTransferRequest sdkRequest =(NeutralOriginalVirtualIoTransferRequest)releaseOwner!.GetType().GetField("Request")!.GetValue(releaseOwner)!;
            NeutralOriginalVirtualIoTransferReceipt sdkReceipt =(NeutralOriginalVirtualIoTransferReceipt)releaseOwner.GetType().GetField("Published")!.GetValue(releaseOwner)!;
            if(mode=="release-source-fresh")Assert.False(sdk.ContinueOriginalVirtualIoSnapshotRelease(sdkRequest,sdkReceipt).IsSuccess);
            releaseOwner.GetType().GetField("ReleaseRequest")!.SetValue(releaseOwner,originalReleaseRequest);
            Assert.False(sdk.ResolveOriginalVirtualIoSnapshotRelease(sdkRequest,sdkReceipt).IsSuccess); // Pure query never starts release.
            Assert.False(sdk.ContinueOriginalVirtualIoSnapshotRelease((NeutralOriginalVirtualIoTransferRequest)Clone(sdkRequest),sdkReceipt).IsSuccess);
            Assert.False(sdk.ContinueOriginalVirtualIoSnapshotRelease(sdkRequest,(NeutralOriginalVirtualIoTransferReceipt)Clone(sdkReceipt)).IsSuccess);
            Assert.Equal(0,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));
            Assert.Equal(new byte[]{71,73},((ExternalOriginalVirtualIoSnapshotReleaseRequest)originalReleaseRequest!).Transfer.CopyReadData());
            Assert.True(host.AdvancePendingResult().IsSuccess); // Explicit exact cleanup continuation, not query-side effect.
            Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));
            Assert.Equal(2,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
            Assert.Equal(1024UL,Used());Assert.Empty(invocationOwners);
            Assert.Throws<InvalidOperationException>(()=>((ExternalOriginalVirtualIoSnapshotReleaseRequest)originalReleaseRequest!).Transfer.CopyReadData());
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0UL,Used());
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));
            Assert.Equal(0x7C,foreign.Span[0]);return;
        }
        if(stage is not null)
        {
            stage.After=null;stage.Observe=null;
            await Assert.ThrowsAnyAsync<Exception>(async()=>await read);
            Assert.Equal(1,bridgeObserved);Assert.True(bridgeLocksFree);
            Assert.Equal(2,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
            Assert.Equal(bridgeSnapshot && !bridgePreRelease?1:0,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));
            Assert.Equal(1026UL,Used());Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            Assert.Equal(0x7C,foreign.Span[0]);
            Assert.False(host.AdvancePendingResult().IsSuccess);Assert.Equal(1026UL,Used());
            if(bridgeInventory is not null)foreach(object original in bridgeInventory)bridgeHistory!.Add(original);
            else bridgeTransfer!.GetType().GetField(bridgeRawFieldName)!.SetValue(bridgeTransfer,bridgeRaw);
            Assert.True(host.AdvancePendingResult().IsSuccess);
            Assert.Equal(2,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));
            Assert.Equal(1024UL,Used());Assert.Empty(invocationOwners);
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0UL,Used());
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);Assert.Equal(0x7C,foreign.Span[0]);return;
        }
        SingPlus.Sip.Native.BoundedBytes detached=default;
        if(first.IsSuccess){
            VirtualIoTransferResponse response =await read;detached=response.Data;Assert.Equal(new byte[]{71,73},response.Data.ToArray());
            Assert.Null(ReadFact().GetType().GetField("SessionResponse",Private)!.GetValue(ReadFact()));
            if(generatedWrite)Assert.Single(invocationOwners);else Assert.Empty(invocationOwners);
            if(publishedOriginal is not null)
            {
                Assert.True(kernel.ReadOriginalSessionPublication(publishedOriginal).IsSuccess);
                Assert.False(kernel.ReadOriginalSessionPublication((EndpointSessionInvocationRegistry.OriginalPublication)Clone(publishedOriginal)).IsSuccess);
                Assert.True(kernel.RetireSessionVirtualIoResponse(publishedOriginal).IsSuccess);
            }
        }
        else await Assert.ThrowsAnyAsync<Exception>(async()=>await read);
        if(mode=="exact")
        {
            foreach(object holder in new object[]{sdk,provider})
            {
                IDictionary registry =(IDictionary)holder.GetType().GetField(ReferenceEquals(holder,sdk)?"originalIo":"_originalProviderVirtualIo",Private)!.GetValue(holder)!;
                object ioOwner =Assert.Single(registry.Values.Cast<object>());
                object fact =((IEnumerable)ioOwner.GetType().GetProperty("Transfers")!.GetValue(ioOwner)!).Cast<object>().Last();
                object transferRequest =fact.GetType().GetField("Request")!.GetValue(fact)!;
                FieldInfo transferField =fact.GetType().GetField("Published")!; object exactTransfer =transferField.GetValue(fact)!;
                FieldInfo transferRawField =fact.GetType().GetField("Raw")!; object exactRaw =transferRawField.GetValue(fact)!;
                MethodInfo transferResolver =holder.GetType().GetMethod("ResolveOriginalVirtualIoTransfer")!;
                bool ResolveTransfer(){ object resolved =transferResolver.Invoke(holder,[transferRequest])!;return (bool)resolved.GetType().GetProperty("IsSuccess")!.GetValue(resolved)!;}
                transferRawField.SetValue(fact,null);Assert.False(ResolveTransfer());
                transferRawField.SetValue(fact,Clone(exactRaw));Assert.False(ResolveTransfer());
                transferRawField.SetValue(fact,exactRaw);Assert.True(ResolveTransfer());
                transferField.SetValue(fact,Clone(exactTransfer));Assert.False(ResolveTransfer());
                transferField.SetValue(fact,exactTransfer);Assert.True(ResolveTransfer());
                Assert.Equal(2,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
                FieldInfo rawField =fact.GetType().GetField("ReleaseRaw")!;
                object retained =rawField.GetValue(fact)!;
                object request =fact.GetType().GetField("Request")!.GetValue(fact)!;
                object transfer =fact.GetType().GetField("Published")!.GetValue(fact)!;
                MethodInfo resolver =holder.GetType().GetMethod("ResolveOriginalVirtualIoSnapshotRelease")!;
                bool Resolve(){ object result =resolver.Invoke(holder,[request,transfer])!;return (bool)result.GetType().GetProperty("IsSuccess")!.GetValue(result)!;}
                rawField.SetValue(fact,Clone(retained)); Assert.False(Resolve());
                rawField.SetValue(fact,retained); Assert.True(Resolve());
                if(ReferenceEquals(holder,sdk))
                {
                    FieldInfo field =fact.GetType().GetField("ReleaseRequest")!; object? original =field.GetValue(fact);
                    field.SetValue(fact,null);Assert.False(Resolve());field.SetValue(fact,original);Assert.True(Resolve());
                }
                Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));Assert.Equal(1024UL,Used());
            }
        }
        Assert.Equal(first.IsSuccess ? 1024UL : 1026UL,Used());
        Assert.Equal(mode=="before-release-effect" || canceledRead ? 0 : 1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));Assert.Equal(generatedWrite ? 3 : 2,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
        if(generatedWrite)
        {
            Assert.Single(invocationOwners);Assert.Equal(1024UL,Used());
            ValueTask closing =client.CloseIoAsync(new(io.Id,io.Generation));Assert.True(host.ProcessNext().IsSuccess);await closing;
            Assert.DoesNotContain(invocationOwners.Values.Cast<object>(),r=>r.GetType().GetProperty("OriginalVirtualIoTransfer")!.GetValue(r) is not null);
            Assert.Equal(512UL,Used());Assert.True(kernel.CloseVirtualIo(io).IsSuccess);
            Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
            Assert.Equal(3,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0UL,Used());
            Assert.Equal(new byte[]{71,73},detached.ToArray());Assert.Equal(0x7C,foreign.Span[0]);
            return;
        }
        if(canceledRead)
        {
            Assert.Single(invocationOwners);
            Assert.Null(spy.ReleaseRequest);
            Assert.False(host.AdvancePendingResult().IsSuccess);Assert.Equal(1026UL,Used());
            spy.Fault="exact";
            spy.ReleaseQueryLoss=mode=="canceled-read-release-query";
            spy.ReleaseReplyLoss=spy.ReleaseQueryLoss;
            Assert.Equal(!spy.ReleaseQueryLoss,host.AdvancePendingResult().IsSuccess);
            if(spy.ReleaseQueryLoss)
            {
                Assert.Equal(1026UL,Used());spy.ReleaseQueryLoss=false;spy.ReleaseReplyLoss=false;
                Assert.True(host.AdvancePendingResult().IsSuccess);
            }
            Assert.Null(ReadFact().GetType().GetField("SessionResponse",Private)!.GetValue(ReadFact()));
            if(generatedWrite)Assert.Single(invocationOwners);else Assert.Empty(invocationOwners);
            Assert.Equal(1024UL,Used());Assert.Equal(generatedWrite ? 3 : 2,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
            Assert.Equal(0,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
            Assert.True(kernel.TransferVirtualIo(owner,new(io,0,true,1,new byte[]{79})).IsSuccess);
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0UL,Used());
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));Assert.Equal(0x7C,foreign.Span[0]);
            return;
        }
        Assert.NotNull(spy.ReleaseRequest);
        if(mode=="before-release-effect")Assert.Equal(new byte[]{71,73},spy.ReleaseRequest!.Transfer.CopyReadData());
        else Assert.Throws<InvalidOperationException>(()=>spy.ReleaseRequest!.Transfer.CopyReadData());
        ExternalOriginalVirtualIoSnapshotReleaseRequest copied =(ExternalOriginalVirtualIoSnapshotReleaseRequest)Clone(spy.ReleaseRequest!);
        Assert.Null(spy.Inner.ReleaseOriginalVirtualIoSnapshot(copied));Assert.Null(spy.Inner.ResolveOriginalVirtualIoSnapshotRelease(copied));
        if(mode!="before-release-effect")Assert.Null(spy.Inner.ReleaseOriginalVirtualIoSnapshot(new(spy.ReleaseRequest!.Transfer)));
        spy.After=null;spy.ReleaseQueryLoss=false;spy.ReleaseReplyLoss=false;
        if(transferRawOwner is not null)
        {
            Assert.False(host.AdvancePendingResult().IsSuccess);Assert.Equal(1026UL,Used());
            Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));
            Assert.Equal(2,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            Assert.Equal(0x7C,foreign.Span[0]);
            transferRawOwner.GetType().GetField("Raw")!.SetValue(transferRawOwner,originalTransferRaw);
        }
        if(mode is "release-request-loss" or "native-release-request-loss")
        {
            Assert.False(host.AdvancePendingResult().IsSuccess);Assert.Equal(1026UL,Used());
            Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));
            Assert.Null(releaseOwner!.GetType().GetField("ReleaseRequest")!.GetValue(releaseOwner));
            releaseOwner.GetType().GetField("ReleaseRequest")!.SetValue(releaseOwner,originalReleaseRequest);
        }
        if(readFact is not null)readFact.GetType().GetField("ChargeOwner",Private)!.SetValue(readFact,originalCharge);
        if(mode is "copy" or "before-release-effect")
        {
            Assert.False(host.AdvancePendingResult().IsSuccess);Assert.Equal(1026UL,Used());
            Assert.Null(ReadFact().GetType().GetField("SessionResponse",Private)!.GetValue(ReadFact()));
            Assert.True((bool)ReadFact().GetType().GetField("SessionResponseRetired",Private)!.GetValue(ReadFact())!);
            Assert.Single(invocationOwners);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(1026UL,Used());
        }
        else
        {
            if(!first.IsSuccess)Assert.True(host.AdvancePendingResult().IsSuccess);
            Assert.Equal(1024UL,Used());Assert.Equal(0,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
            Assert.True(kernel.TransferVirtualIo(owner,new(io,0,true,1,new byte[]{79})).IsSuccess); // Backing still usable.
            KernelResult closed =kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
            Assert.Equal(0UL,Used());Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        }
        if(first.IsSuccess)Assert.Equal(new byte[]{71,73},detached.ToArray());
        Assert.Equal(mode=="before-release-effect" ? 0 : 1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));Assert.Equal(0x7C,foreign.Span[0]);
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SnapshotCleanupInterlockPreservesIndependentIoOnSameChild(bool peerUnknown)
    {
        IReleaseRuntime external =DispatchProxy.Create<IReleaseRuntime,CloseSpy>(); CloseSpy spy =(CloseSpy)(object)external;spy.Inner=new();
        HybridCpuExecutableChildAdapter sdk =new HybridCpuExecutableChildAdapter(spy.Inner,external);
        NeutralDomainRuntimeFacade model =new NeutralDomainRuntimeFacade(); HybridCpuPlatformAuthorityProvider provider =new HybridCpuPlatformAuthorityProvider(model,sdk);
        RuntimeKernel kernel =new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel,3071,30710); ProcessHandle service =TestFixtures.Create(kernel,3072,30720).Handle;
        ProcessHandle admin =TestFixtures.Create(kernel,3073,30730).Handle;
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(service,32).Value!;foreign.Span[0]=0x7C;
        CapabilityId administration =kernel.MintCapability(new(30730),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(admin,administration,owner,"independent-read-release",[new(ServiceBudgetDimension.OwnedMemoryBytes,peerUnknown?1539UL:1538UL)]).Value!.ProcessBudget;
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        OwnedBuffer<byte> region =kernel.AllocateBuffer<byte>(owner,512).Value!;
        CapabilityId create =kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain =kernel.CreateVirtualDomain(owner,create,new(1,4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess);
        object rootOwner =Assert.Single(((IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains",Private)!.GetValue(kernel.PlatformAuthority)!).Values.Cast<object>());
        PlatformDomainBinding root =(PlatformDomainBinding)rootOwner.GetType().GetProperty("Binding")!.GetValue(rootOwner)!;
        VirtualIoBinding Bind(string id)
        {
            CapabilityId cap =kernel.MintCapability(process.DomainId,owner,ResourceKind.Device,id,CapabilityRights.Read|CapabilityRights.Write|CapabilityRights.Configure).Value!.CapabilityId;
            PlatformDeviceLease device =kernel.BindPlatformDevice(owner,root,cap,PlatformDeviceRights.Read|PlatformDeviceRights.Write).Value!;
            return kernel.BindVirtualIo(owner,domain.Domain,device,new(PlatformDeviceRights.Read|PlatformDeviceRights.Write,512)).Value;
        }
        VirtualIoBinding io =Bind("device:release-first"); VirtualIoBinding peer =Bind("device:release-peer");Assert.Equal(1536UL,Used());
        Assert.True(kernel.TransferVirtualIo(owner,new(io,0,true,2,[71,73])).IsSuccess);
        if(peerUnknown)Assert.True(kernel.TransferVirtualIo(owner,new(peer,0,true,1,[0x6B])).IsSuccess);
        KernelVirtualIoTransferRequest? peerOriginal=null;
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"independent-read-release",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(owner,descriptor,[]).Value; RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        IVirtualizationService client =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        int callbacks =0;bool sameCloseDenied=false,sameTransferDenied=false,peerWrote=false,locksFree=false;string? peerFailure=null;
        ulong during=0;
        spy.After=name=>
        {
            if(name!="ReleaseOriginalVirtualIoSnapshot")return;
            callbacks++;
            locksFree=!Monitor.IsEntered(kernel.GetType().GetField("_virtualIoGate",Private)!.GetValue(kernel)!) &&
                !Monitor.IsEntered(typeof(HybridCpuExecutableChildAdapter).GetField("sync",Private)!.GetValue(sdk)!) &&
                !Monitor.IsEntered(typeof(HybridCpuPlatformAuthorityProvider).GetField("_childCreateCommitGate",Private)!.GetValue(provider)!);
            sameCloseDenied=!kernel.CloseVirtualIo(io).IsSuccess;
            sameTransferDenied=!kernel.TransferVirtualIo(owner,new(io,0,true,1,[0xEE])).IsSuccess;
            if(peerUnknown){spy.Fault="kernel-read-query-loss";peerOriginal=new(peer,0,false,1);}
            KernelResult<PlatformOriginalVirtualIoTransferReceipt> write =kernel.TransferVirtualIo(owner,peerOriginal ?? new(peer,0,true,1,[0x6B]));peerWrote=write.IsSuccess;peerFailure=write.Message;
            during=Used();
        };
        ValueTask<VirtualIoTransferResponse> read =client.TransferIoAsync(new(io.Id,io.Generation,0,false,2,default));
        KernelResult handled =host.ProcessNext();Assert.True(handled.IsSuccess,handled.Message);
        VirtualIoTransferResponse response =await read;spy.After=null;spy.Fault="exact";
        Assert.Equal(1,callbacks);Assert.True(locksFree);Assert.True(sameCloseDenied);Assert.True(sameTransferDenied);if(peerUnknown)Assert.False(peerWrote);else Assert.True(peerWrote,peerFailure);
        Assert.Equal(peerUnknown?1539UL:1538UL,during);Assert.Equal(peerUnknown?1537UL:1536UL,Used());
        Assert.Equal(peerUnknown?4:3,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));
        Assert.Equal(0,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));Assert.Equal(new byte[]{71,73},response.Data.ToArray());
        Assert.True(kernel.CloseVirtualIo(io).IsSuccess);Assert.Equal(peerUnknown?1025UL:1024UL,Used());
        KernelResult<PlatformOriginalVirtualIoTransferReceipt> peerRead =kernel.TransferVirtualIo(owner,peerOriginal ?? new(peer,0,false,1));Assert.True(peerRead.IsSuccess,peerRead.Message);Assert.Equal(new byte[]{0x6B},peerRead.Value!.CopyReadData());
        Assert.Equal(1025UL,Used());Assert.True(kernel.CloseVirtualIo(peer).IsSuccess);Assert.Equal(512UL,Used());
        Assert.Equal(4,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));Assert.Equal(2,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0UL,Used());
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        Assert.Equal(0,model.ActiveDeviceLeaseCount);Assert.Equal(0,model.ActiveBindingCount);
        Assert.Equal(new byte[]{71,73},response.Data.ToArray());Assert.True(foreign.IsValid);Assert.Equal(0x7C,foreign.Span[0]);
    }
    private static object Clone(object value)=>typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(value,null)!;
}








