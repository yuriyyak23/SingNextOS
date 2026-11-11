using SingPlus.Sip.Virtualization;
using SingPlus.Sip.Native;
using System.Collections;
using System.Reflection;
using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;
using SingPlus.Sip;
namespace SingPlus.Tests.Platform;
public sealed class ExactVirtualIoBackingConsumerTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public interface IBridgeBindProvider : IPlatformAuthorityProvider,IPlatformFeatureProvider,IPlatformDomainExecutionProvider,
        IPlatformChildCreateDispositionProvider,IPlatformOriginalChildCreateProvider,IPlatformOriginalAdmissionResolutionProvider,
        IPlatformOriginalDeviceProvider,IPlatformVirtualIoProvider,IPlatformVirtualIoPrivateBackingProvider,
        IPlatformOriginalVirtualIoBindProvider,IPlatformOriginalVirtualIoCloseProvider,IPlatformProviderIncarnationSource,
        IPlatformOriginalDomainClosureProvider,IPlatformOriginalDomainClosureContinuationProvider { }
    public class BridgeBindSpy : DispatchProxy
    {
        public HybridCpuPlatformAuthorityProvider Inner=null!;public Action? BeforeSource;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(method!.Name=="get_CurrentIncarnation")BeforeSource?.Invoke();
            try{return method.Invoke(Inner,args);}
            catch(TargetInvocationException exception) when(exception.InnerException is not null)
            {global::System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();throw;}
        }
    }
    [Theory]
    [InlineData("bind-duplicate-device")] [InlineData("bind-duplicate-user")]
    [InlineData("duplicate-device")] [InlineData("duplicate-user")] [InlineData("duplicate-child")]
    [InlineData("kernel-read-capacity")] [InlineData("kernel-read-fact-copy")]
    [InlineData("kernel-read-model-incarnation")] [InlineData("kernel-read-adapter-copy")] [InlineData("kernel-read-provider-copy")] [InlineData("kernel-read-native-generation")] [InlineData("kernel-read-adapter-revision")] [InlineData("kernel-read-source-revision")] [InlineData("kernel-read-history")]
    [InlineData("kernel-read-query-loss")] [InlineData("kernel-read-charge-route")]
    [InlineData("session-read-auto-teardown")] [InlineData("session-read-partial")] [InlineData("session-read-owner-copy")] [InlineData("session-read-unknown")] [InlineData("session-binding")]
    [InlineData("exact")] [InlineData("history-loss")]
    [InlineData("device-bind-lost")] [InlineData("io-bind-lost")]
    [InlineData("io-close-lost")] [InlineData("device-close-lost")]
    [InlineData("io-bind-copy")] [InlineData("io-close-copy")]
    [InlineData("device-close-copy")] [InlineData("source-aba")]
    [InlineData("close-reentry")] [InlineData("close-query-throw")]
    [InlineData("charge-route-loss")] [InlineData("charge-history-loss")]
    [InlineData("close-query-recover")] [InlineData("device-query-recover")]
    [InlineData("io-close-lost-query-recover")] [InlineData("device-close-lost-query-recover")]
    [InlineData("close-query-aba")]
    [InlineData("device-bind-null")] [InlineData("device-bind-copy")]
    [InlineData("io-bind-query-throw")] [InlineData("bind-containment-lost")]
    [InlineData("bind-containment-query-throw")] [InlineData("bind-containment-copy")]
    [InlineData("bind-history-loss")]
    [InlineData("bind-charge-route-loss")] [InlineData("bind-bridge-history-loss")]
    [InlineData("bind-containment-aba")]
    [InlineData("bind-bridge-cleanup-copy")] [InlineData("bind-bridge-cleanup-loss")]
    [InlineData("bind-sdk-containment-copy")] [InlineData("bind-sdk-containment-loss")] [InlineData("bind-sdk-containment-throw")]
    [InlineData("refused-sdk-noeffect-copy")] [InlineData("refused-sdk-noeffect-loss")]
    [InlineData("bind-provider-cleanup-copy")] [InlineData("bind-provider-cleanup-loss")]
    [InlineData("bind-publication-Device-copy")] [InlineData("bind-publication-Device-loss")]
    [InlineData("bind-publication-Binding-copy")] [InlineData("bind-publication-Binding-loss")]
    [InlineData("provider-bind-admission-loss")] [InlineData("provider-bind-admission-substitute")]
    [InlineData("bridge-bind-observation-loss")] [InlineData("bridge-bind-observation-substitute")]
    [InlineData("loan-closure-copy")] [InlineData("loan-closure-loss")]
    [InlineData("loan-history-duplicate")]
    [InlineData("child-io-history-loss")] [InlineData("child-io-history-copy")] [InlineData("child-io-history-unconsumed")]
    [InlineData("child-io-history-closure-loss")]
    [InlineData("child-io-history-route-loss")]
    [InlineData("device-history-duplicate")]
    [InlineData("device-history-lost-reply")]
    [InlineData("device-history-root-envelope-loss")]
    [InlineData("device-history-root-result-copy")]
    [InlineData("device-history-root-request-substitution")]
    [InlineData("device-history-parent-loss")]
    [InlineData("device-history-parent-substitution")]
    [InlineData("refused")] [InlineData("refused-lost")]
    [InlineData("refused-query-throw")] [InlineData("refused-copy")]
    [InlineData("refused-op-exhausted")]
    public void ActualNativeLoanAndExternalBackingControlProcessRegionAndChargeReclaim(string fault)
    {
        NeutralDomainRuntimeFacade model = new NeutralDomainRuntimeFacade();
        bool deviceHistory =fault.StartsWith("device-history-",StringComparison.Ordinal);
        bool providerBind =fault.StartsWith("provider-bind-",StringComparison.Ordinal);
        bool bridgeBind =fault.StartsWith("bridge-bind-",StringComparison.Ordinal);
        NativeTransferRawCustodyConsumerTests.ISourceRuntime sourceProxy =DispatchProxy.Create<NativeTransferRawCustodyConsumerTests.ISourceRuntime,NativeTransferRawCustodyConsumerTests.SourceSpy>();
        NativeTransferRawCustodyConsumerTests.SourceSpy sourceSpy =(NativeTransferRawCustodyConsumerTests.SourceSpy)(object)sourceProxy;sourceSpy.Inner=model;
        HybridCpuExternalRuntime native = new HybridCpuExternalRuntime();
        IOriginalBackingRuntime proxy = DispatchProxy.Create<IOriginalBackingRuntime, BackingSpy>();
        BackingSpy spy = (BackingSpy)proxy; spy.Inner = native; spy.Fault = fault;
        if(providerBind)spy.Fault="exact";
        bool sdkCleanup =fault.StartsWith("bind-sdk-",StringComparison.Ordinal)||fault.StartsWith("refused-sdk-",StringComparison.Ordinal);
        bool providerCleanup =fault.StartsWith("bind-provider-cleanup-",StringComparison.Ordinal);
        bool bindPublication =fault.StartsWith("bind-publication-",StringComparison.Ordinal);
        if(bindPublication)spy.Fault="exact";
        if(providerCleanup)spy.Fault="io-bind-query-throw";
        if(sdkCleanup)spy.Fault=fault.StartsWith("refused",StringComparison.Ordinal)?"refused":"io-bind-query-throw";
        if(fault.StartsWith("bind-bridge-cleanup-",StringComparison.Ordinal))spy.Fault="io-bind-query-throw";
        ConstructorInfo constructor = typeof(HybridCpuExecutableChildAdapter).GetConstructor(Private, null,
            [typeof(IHybridCpuExternalRuntime), typeof(IHybridCpuChildDomainRuntimeV3), typeof(ISemanticTraceSinkV1)], null)!;
        ExactArtifactReleaseConsumerTests.IOriginalRootRuntime rootProxy =DispatchProxy.Create<ExactArtifactReleaseConsumerTests.IOriginalRootRuntime,ExactArtifactReleaseConsumerTests.RootSpy>();
        ExactArtifactReleaseConsumerTests.RootSpy rootSpy =(ExactArtifactReleaseConsumerTests.RootSpy)rootProxy;rootSpy.Inner=native;
        HybridCpuExecutableChildAdapter adapter = (HybridCpuExecutableChildAdapter)constructor.Invoke([bindPublication||sdkCleanup||fault=="kernel-read-native-generation"?rootProxy:native, proxy, null]);
        spy.After = name =>
        {
            Assert.False(Monitor.IsEntered(typeof(HybridCpuExecutableChildAdapter).GetField("sync", Private)!.GetValue(adapter)!));
            if (fault == "source-aba" && name == "BindOriginalVirtualIo" || fault == "close-query-aba" && name == "ResolveOriginalVirtualIoClosure")
            {
                IDictionary children = (IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("children", Private)!.GetValue(adapter)!;
                object child = Assert.Single(children.Values.Cast<object>()); PropertyInfo state = child.GetType().GetProperty("State")!;
                state.SetValue(child, state.GetValue(child));
            }
            if (fault.StartsWith("bind-duplicate-", StringComparison.Ordinal) && name == "BindOriginalVirtualIo")
            {
                object nativeRoot = Assert.Single(((IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(native)!).Values.Cast<object>());
                IList devicesHistory = (IList)nativeRoot.GetType().GetProperty("OriginalParentDevices")!.GetValue(nativeRoot)!;
                object backingOwner = Assert.Single(devicesHistory.Cast<object>());
                byte[] backing = (byte[])backingOwner.GetType().GetField("Backing")!.GetValue(backingOwner)!;
                backing[0] = 17;
                object historyOwner = fault == "bind-duplicate-device" ? nativeRoot : backingOwner;
                string historyName = fault == "bind-duplicate-device" ? "OriginalParentDevices" : "Users";
                string issuedName = fault == "bind-duplicate-device" ? "OriginalParentDevicesIssued" : "UsersIssued";
                IList history = (IList)historyOwner.GetType().GetProperty(historyName)!.GetValue(historyOwner)!;
                history.Add(history[0]);
                if (historyOwner.GetType().GetField(issuedName) is { } issued) issued.SetValue(historyOwner, (ulong)history.Count);
                else historyOwner.GetType().GetProperty(issuedName)!.SetValue(historyOwner, (ulong)history.Count);
            }
            if (fault == "bind-history-loss" && name == "BindOriginalVirtualIo")
            {
                IDictionary children = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(native)!;
                object child = Assert.Single(children.Values.Cast<object>());
                ((IList)child.GetType().GetProperty("OriginalVirtualIo")!.GetValue(child)!).Clear();
            }
        };
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(providerBind||deviceHistory?sourceProxy:model, adapter);
        IBridgeBindProvider bridgeProxy =DispatchProxy.Create<IBridgeBindProvider,BridgeBindSpy>(); BridgeBindSpy bridgeSpy =(BridgeBindSpy)(object)bridgeProxy;bridgeSpy.Inner=provider;
        RuntimeKernel kernel = new RuntimeKernel(bridgeBind?bridgeProxy:provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 783, 1127);
        ProcessHandle admin = TestFixtures.Create(kernel, 99, 999).Handle;
        CapabilityId budgetCap = kernel.MintCapability(new(999), admin, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "original-io-backing", [new(ServiceBudgetDimension.OwnedMemoryBytes, deviceHistory||fault=="loan-history-duplicate"?1536UL:fault is "exact" or "session-binding" or "session-read-unknown" or "session-read-owner-copy" or "session-read-partial" or "session-read-auto-teardown" || fault.StartsWith("kernel-read-",StringComparison.Ordinal) && fault!="kernel-read-capacity" ? 1028UL : 1024UL)]).Value!.ProcessBudget;
        OwnedBuffer<byte> region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        CapabilityId create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        KernelResult<VirtualDomainAuthoritySet> created = kernel.CreateVirtualDomain(owner, create, new(1, 4096)); Assert.True(created.IsSuccess, created.Message);
        VirtualDomainAuthoritySet domain = created.Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        IDictionary roots = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", Private)!.GetValue(kernel.PlatformAuthority)!;
        object rootOwner = Assert.Single(roots.Values.Cast<object>());
        PlatformDomainBinding root = (PlatformDomainBinding)rootOwner.GetType().GetProperty("Binding")!.GetValue(rootOwner)!;
        CapabilityId source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Device, "device:original-io", CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!.CapabilityId;
        KernelResult<PlatformDeviceLease> deviceAdmission = kernel.BindPlatformDevice(owner, root, source, PlatformDeviceRights.Read | PlatformDeviceRights.Write); Assert.True(deviceAdmission.IsSuccess, deviceAdmission.Message);
        PlatformDeviceLease device = deviceAdmission.Value!;
        KernelResult<VirtualIoBinding> bound;
        if(fault is "session-binding" or "session-read-unknown" or "session-read-owner-copy" or "session-read-partial" or "session-read-auto-teardown")
        {
            ProcessHandle service =TestFixtures.Create(kernel,106,1006).Handle;
            ProtocolDefinitionV1 protocol =IVirtualizationServiceProtocol.CreateDefinition();
            ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"actual-session-bind",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,IVirtualizationServiceResponseProtocol.Definition,[]).Value!;
            EndpointSessionHandle session =kernel.OpenSession(owner,descriptor,[]).Value;
            RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
            IVirtualizationService client =IVirtualizationServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
            Task<VirtualIoHandleResponse> invalidAccess =client.BindIoAsync(new(domain.Domain,device.LeaseId.Value,device.Generation.Value,(VirtualIoAccess)255,512)).AsTask();
            Assert.False(host.ProcessNext().IsSuccess);Assert.ThrowsAny<Exception>(()=>invalidAccess.GetAwaiter().GetResult());
            Assert.Equal(0,spy.Calls.GetValueOrDefault("BindOriginalParentDevice"));
            ProcessHandle stranger =TestFixtures.Create(kernel,107,1007).Handle;
            EndpointSessionHandle foreignSession =kernel.OpenSession(stranger,descriptor,[]).Value;
            RuntimeVirtualizationServiceHost foreignHost =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,foreignSession).Value!;
            IVirtualizationService foreignClient =IVirtualizationServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel,stranger,foreignSession));
            Task<VirtualIoHandleResponse> foreignBind =foreignClient.BindIoAsync(new(domain.Domain,device.LeaseId.Value,device.Generation.Value,VirtualIoAccess.Read,512)).AsTask();
            Assert.False(foreignHost.ProcessNext().IsSuccess);Assert.ThrowsAny<Exception>(()=>foreignBind.GetAwaiter().GetResult());
            Assert.Equal(0,spy.Calls.GetValueOrDefault("BindOriginalParentDevice"));
            Task<VirtualIoHandleResponse> opened =client.BindIoAsync(new(domain.Domain,device.LeaseId.Value,device.Generation.Value,VirtualIoAccess.Read | VirtualIoAccess.Write,512)).AsTask();
            KernelResult issued =host.ProcessNext();Assert.True(issued.IsSuccess,issued.Message);
            VirtualIoHandleResponse handle =opened.GetAwaiter().GetResult();bound=KernelResult<VirtualIoBinding>.Ok(new(handle.IoId,handle.Generation));
            Task<VirtualIoTransferResponse> sent =client.TransferIoAsync(new(handle.IoId,handle.Generation,0,true,2,new BoundedBytes(new byte[]{71,73}))).AsTask();
            Assert.True(host.ProcessNext().IsSuccess);Assert.True(sent.GetAwaiter().GetResult().Completed);
            if(fault is "session-read-unknown" or "session-read-owner-copy" or "session-read-partial" or "session-read-auto-teardown")
            {
                spy.Fault="kernel-read-query-loss";
                Task<VirtualIoTransferResponse> lost =client.TransferIoAsync(new(handle.IoId,handle.Generation,0,false,2,default)).AsTask();
                Assert.False(host.ProcessNext().IsSuccess);Assert.ThrowsAny<Exception>(()=>lost.GetAwaiter().GetResult());
                Assert.Equal(2,spy.Calls["TransferOriginalVirtualIo"]);
                Assert.Equal(1026UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
                if(fault=="session-read-auto-teardown")
                {
                    spy.Fault="exact";
                    KernelResult autoCallerTeardown =kernel.TerminateProcess(owner);Assert.True(autoCallerTeardown.IsSuccess,autoCallerTeardown.Message);
                    Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                    Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
                    Assert.Equal(2,spy.Calls["TransferOriginalVirtualIo"]);Assert.Equal(1,spy.Calls["CloseOriginalVirtualIo"]);
                    Assert.True(kernel.Processes.Resolve(stranger).IsSuccess);Assert.True(kernel.Processes.Resolve(service).IsSuccess);
                    Assert.True(kernel.CloseSession(stranger,foreignSession).IsSuccess);
                    return;
                }
                Assert.True(kernel.CloseSession(stranger,foreignSession).IsSuccess);
                spy.Fault="exact";
                bool sessionAlreadyClosed =false;
                if(fault=="session-read-partial")
                {
                    object ioOwner =Assert.Single(((IDictionary)kernel.GetType().GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>());
                    IList history =(IList)ioOwner.GetType().GetProperty("Transfers",Private)!.GetValue(ioOwner)!;
                    object readFact =Assert.Single(history.Cast<object>().Where(f=>!(bool)f.GetType().GetField("Request",Private)!.GetValue(f)!.GetType().GetProperty("Write",Private)!.GetValue(f.GetType().GetField("Request",Private)!.GetValue(f))!));
                    FieldInfo chargeLink =readFact.GetType().GetField("ChargeOwner",Private)!; object partialReadCharge =chargeLink.GetValue(readFact)!;
                    chargeLink.SetValue(readFact,typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(partialReadCharge,null));
                    Assert.False(kernel.CloseVirtualIo(bound.Value!).IsSuccess);
                    Assert.Equal(1026UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                    Assert.True(kernel.CloseSession(owner,session).IsSuccess);sessionAlreadyClosed=true;
                    chargeLink.SetValue(readFact,partialReadCharge);
                    Assert.True(kernel.CloseVirtualIo(bound.Value!).IsSuccess);
                    Assert.Equal(1,spy.Calls["CloseOriginalVirtualIo"]);
                }
                else if(fault=="session-read-owner-copy")
                {
                    object invocations =kernel.GetType().GetField("_endpointSessionInvocationRegistry",Private)!.GetValue(kernel)!;
                    IDictionary records =(IDictionary)invocations.GetType().GetField("_ownedRecords",Private)!.GetValue(invocations)!;
                    object invocationOwner =Assert.Single(records.Values.Cast<object>().Where(r=>(bool)r.GetType().GetProperty("AcceptedResponseCancelled")!.GetValue(r)!));
                    PropertyInfo ownerLink =invocationOwner.GetType().GetProperty("OriginalVirtualIoTransfer")!;
                    object originalTransfer =ownerLink.GetValue(invocationOwner)!;
                    EndpointSessionInvocationHandle exactInvocation =(EndpointSessionInvocationHandle)invocationOwner.GetType().GetProperty("Handle")!.GetValue(invocationOwner)!;
                    Assert.False(((EndpointSessionInvocationRegistry)invocations).ConsumeVirtualIoTransferClosure(exactInvocation,stranger,originalTransfer).IsSuccess);
                    Assert.False(((EndpointSessionInvocationRegistry)invocations).ConsumeVirtualIoTransferClosure(exactInvocation,owner,null!).IsSuccess);
                    ownerLink.SetValue(invocationOwner,typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(originalTransfer,null));
                    Assert.False(kernel.CloseVirtualIo(bound.Value!).IsSuccess);
                    Assert.Equal(1026UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                    Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
                    Assert.Equal(1,spy.Calls["CloseOriginalVirtualIo"]);
                    ownerLink.SetValue(invocationOwner,originalTransfer);
                    Assert.True(kernel.CloseVirtualIo(bound.Value!).IsSuccess);
                }
                else
                {
                    Task unknownIoClose =client.CloseIoAsync(new(handle.IoId,handle.Generation)).AsTask();
                    Assert.True(host.ProcessNext().IsSuccess);unknownIoClose.GetAwaiter().GetResult();
                }
                Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                if(!sessionAlreadyClosed)Assert.True(kernel.CloseSession(owner,session).IsSuccess);
                KernelResult unknownOwnerTermination =kernel.TerminateProcess(owner);Assert.True(unknownOwnerTermination.IsSuccess,unknownOwnerTermination.Message);
                Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
                Assert.Equal(2,spy.Calls["TransferOriginalVirtualIo"]);Assert.Equal(1,spy.Calls["CloseOriginalVirtualIo"]);
                return;
            }
            Task<VirtualIoTransferResponse> received =client.TransferIoAsync(new(handle.IoId,handle.Generation,0,false,2,default)).AsTask();
            Assert.True(host.ProcessNext().IsSuccess);Assert.Equal(new byte[]{71,73},received.GetAwaiter().GetResult().Data.ToArray());
            Assert.Equal(1026UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Task<VirtualIoTransferResponse> foreignRead =foreignClient.TransferIoAsync(new(handle.IoId,handle.Generation,0,false,2,default)).AsTask();
            Assert.False(foreignHost.ProcessNext().IsSuccess);Assert.ThrowsAny<Exception>(()=>foreignRead.GetAwaiter().GetResult());
            Task foreignClose =foreignClient.CloseIoAsync(new(handle.IoId,handle.Generation)).AsTask();
            Assert.False(foreignHost.ProcessNext().IsSuccess);Assert.ThrowsAny<Exception>(()=>foreignClose.GetAwaiter().GetResult());
            Task<VirtualIoTransferResponse> staleRead =client.TransferIoAsync(new(handle.IoId,handle.Generation+1,0,false,2,default)).AsTask();
            Assert.False(host.ProcessNext().IsSuccess);Assert.ThrowsAny<Exception>(()=>staleRead.GetAwaiter().GetResult());
            Assert.Equal(2,spy.Calls["TransferOriginalVirtualIo"]);Assert.Equal(0,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
            Assert.Equal(1026UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.True(kernel.CloseSession(stranger,foreignSession).IsSuccess);
            Task sessionClose =client.CloseIoAsync(new(handle.IoId,handle.Generation)).AsTask();
            Assert.True(host.ProcessNext().IsSuccess);sessionClose.GetAwaiter().GetResult();
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            KernelResult ownSessionClose =kernel.CloseSession(owner,session);Assert.True(ownSessionClose.IsSuccess,ownSessionClose.Message);
            KernelResult ownTermination =kernel.TerminateProcess(owner);Assert.True(ownTermination.IsSuccess,ownTermination.Message);
            Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
            Assert.Equal(2,spy.Calls["TransferOriginalVirtualIo"]);Assert.Equal(1,spy.Calls["CloseOriginalVirtualIo"]);
            return;
        }
        object? publicationFact=null,publicationRaw=null;FieldInfo? publicationRoute=null;byte[]? publicationBacking=null;
        object? providerBindFact=null,providerBindRaw=null;PropertyInfo? providerBindRoute=null;
        object? bridgeBindFact=null,bridgeBindRaw=null;PropertyInfo? bridgeBindRoute=null;
        if(bridgeBind)bridgeSpy.BeforeSource=()=>
        {
            if(bridgeBindRaw is not null)return;
            object[] devices =((IDictionary)typeof(PlatformAuthorityBridge).GetField("_originalDeviceOwners",Private)!.GetValue(kernel.PlatformAuthority)!).Values.Cast<object>().ToArray();
            if(devices.Length!=1)return;
            object[] facts =((IEnumerable)devices[0].GetType().GetProperty("VirtualIoBindInvocations")!.GetValue(devices[0])!).Cast<object>().ToArray();
            if(facts.Length!=1 || facts[0].GetType().GetProperty("Observation")!.GetValue(facts[0]) is not { } raw)return;
            bridgeBindFact=facts[0];bridgeBindRoute=bridgeBindFact.GetType().GetProperty("Observation")!;bridgeBindRaw=raw;
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate",Private)!.GetValue(kernel.PlatformAuthority)!));
            PlatformAuthorityResult<PlatformProviderVirtualIoLease> result =(PlatformAuthorityResult<PlatformProviderVirtualIoLease>)raw;
            object nativeRoot =Assert.Single(((IDictionary)native.GetType().GetField("registry",Private)!.GetValue(native)!).Values.Cast<object>());
            object deviceOwner =Assert.Single(((IEnumerable)nativeRoot.GetType().GetProperty("OriginalParentDevices")!.GetValue(nativeRoot)!).Cast<object>());
            publicationBacking=(byte[])deviceOwner.GetType().GetField("Backing")!.GetValue(deviceOwner)!;publicationBacking[0]=0x67;
            bridgeBindRoute.SetValue(bridgeBindFact,fault.EndsWith("loss",StringComparison.Ordinal)?null:result with {Value=result.Value! with {LeaseId=new(result.Value!.LeaseId.Value+1)}});
        };
        if(providerBind)sourceSpy.BeforeSource=()=>
        {
            if(providerBindRaw is not null)return;
            object[] candidates =((IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderVirtualIo",Private)!.GetValue(provider)!).Values.Cast<object>().ToArray();
            if(candidates.Length!=1 || candidates[0].GetType().GetProperty("Admission")!.GetValue(candidates[0]) is not { } raw)return;
            providerBindFact=candidates[0];providerBindRoute=providerBindFact.GetType().GetProperty("Admission")!;providerBindRaw=raw;
            Assert.False(Monitor.IsEntered(typeof(HybridCpuPlatformAuthorityProvider).GetField("_childCreateCommitGate",Private)!.GetValue(provider)!));
            object nativeRoot =Assert.Single(((IDictionary)native.GetType().GetField("registry",Private)!.GetValue(native)!).Values.Cast<object>());
            object deviceOwner =Assert.Single(((IEnumerable)nativeRoot.GetType().GetProperty("OriginalParentDevices")!.GetValue(nativeRoot)!).Cast<object>());
            publicationBacking=(byte[])deviceOwner.GetType().GetField("Backing")!.GetValue(deviceOwner)!;publicationBacking[0]=0x65;
            NeutralVirtualizationResult<NeutralVirtualIoLease> result =(NeutralVirtualizationResult<NeutralVirtualIoLease>)raw;
            providerBindRoute.SetValue(providerBindFact,fault.EndsWith("loss",StringComparison.Ordinal)?null:result with {Value=result.Value with {Handle=new(result.Value.Handle.Value+1)}});
        };
        if(bindPublication)rootSpy.BeforeQuery=()=>
        {
            if(publicationRaw is not null)return;
            object[] candidates =((IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("originalIo",Private)!.GetValue(adapter)!).Values.Cast<object>().ToArray();
            if(candidates.Length!=1 || candidates[0].GetType().GetField("Binding")!.GetValue(candidates[0]) is null)return;
            publicationFact=candidates[0];publicationRoute=publicationFact.GetType().GetField(fault.Contains("-Device-",StringComparison.Ordinal)?"Device":"Binding")!;
            publicationRaw=publicationRoute.GetValue(publicationFact);Assert.NotNull(publicationRaw);
            object nativeRoot =Assert.Single(((IDictionary)native.GetType().GetField("registry",Private)!.GetValue(native)!).Values.Cast<object>());
            object deviceOwner =Assert.Single(((IEnumerable)nativeRoot.GetType().GetProperty("OriginalParentDevices")!.GetValue(nativeRoot)!).Cast<object>());
            publicationBacking=(byte[])deviceOwner.GetType().GetField("Backing")!.GetValue(deviceOwner)!;publicationBacking[0]=0x63;
            Assert.False(Monitor.IsEntered(typeof(HybridCpuExecutableChildAdapter).GetField("sync",Private)!.GetValue(adapter)!));
            publicationRoute.SetValue(publicationFact,fault.EndsWith("copy",StringComparison.Ordinal)?typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(publicationRaw,null):null);
        };
        bound = kernel.BindVirtualIo(owner, domain.Domain, device, new(PlatformDeviceRights.Read | PlatformDeviceRights.Write, 512));
        Assert.Equal(1024UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        if(deviceHistory||fault=="loan-history-duplicate")
        {
            Assert.True(bound.IsSuccess,bound.Message);
            PlatformDeviceLease secondDevice =device;
            if(deviceHistory)
            {
                CapabilityId secondCap =kernel.MintCapability(process.DomainId,owner,ResourceKind.Device,"device:second-history",CapabilityRights.Read|CapabilityRights.Write|CapabilityRights.Configure).Value!.CapabilityId;
                secondDevice=kernel.BindPlatformDevice(owner,kernel.QueryVirtualDomainPlatformBinding(owner,domain.Domain).Value!,secondCap,PlatformDeviceRights.Read|PlatformDeviceRights.Write).Value!;
            }
            KernelResult<VirtualIoBinding> second =kernel.BindVirtualIo(owner,domain.Domain,secondDevice,new(PlatformDeviceRights.Read|PlatformDeviceRights.Write,512));Assert.True(second.IsSuccess,second.Message);
            Assert.Equal(1536UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            OwnedBuffer<byte> foreignHistoryRegion =kernel.AllocateBuffer<byte>(admin,8).Value!;foreignHistoryRegion.Span[0]=0x6D;
            Assert.True(kernel.TransferVirtualIo(owner,new(bound.Value!,0,true,1,[0x71])).IsSuccess);
            Assert.True(kernel.TransferVirtualIo(owner,new(second.Value!,0,true,1,[0x72])).IsSuccess);
            object historyNativeRoot =Assert.Single(((IDictionary)native.GetType().GetField("registry",Private)!.GetValue(native)!).Values.Cast<object>());
            object[] backingOwners =((IEnumerable)historyNativeRoot.GetType().GetProperty("OriginalParentDevices")!.GetValue(historyNativeRoot)!).Cast<object>().ToArray();Assert.Equal(2,backingOwners.Length);
            byte[][] historyBackings =backingOwners.Select(value=>(byte[])value.GetType().GetField("Backing")!.GetValue(value)!).ToArray();
            Assert.Equal(0x71,historyBackings[0][0]);Assert.Equal(0x72,historyBackings[1][0]);
            Assert.True(kernel.CloseVirtualIo(bound.Value!).IsSuccess);
            Assert.All(historyBackings[0],value=>Assert.Equal(0,value));Assert.Equal(0x72,historyBackings[1][0]);
            Assert.Equal(1024UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.True(kernel.TransferVirtualIo(owner,new(second.Value!,0,true,1,[0x73])).IsSuccess);
            Assert.Equal(0x73,historyBackings[1][0]);
            Assert.True(kernel.CloseVirtualIo(second.Value!).IsSuccess);
            Assert.All(historyBackings[1],value=>Assert.Equal(0,value));
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            object dependencies =typeof(NeutralDomainRuntimeFacade).GetField("_dependencies",Private)!.GetValue(model)!;
            IDictionary loanDevices =(IDictionary)dependencies.GetType().GetField("Devices",Private)!.GetValue(dependencies)!;
            IList loans;
            if(deviceHistory)
            {
                Assert.Equal(2,loanDevices.Count);Assert.True(kernel.RevokePlatformDevice(owner,device).IsSuccess);Assert.True(kernel.RevokePlatformDevice(owner,secondDevice).IsSuccess);
                IDictionary rootDomains =(IDictionary)dependencies.GetType().GetField("Domains",Private)!.GetValue(dependencies)!;
                object deviceHistoryRoot =Assert.Single(rootDomains.Values.Cast<object>());
                loans=(IList)deviceHistoryRoot.GetType().GetProperty("OriginalDeviceAdmissions")!.GetValue(deviceHistoryRoot)!;
            }
            else
            {
                object deviceOwner =Assert.Single(loanDevices.Values.Cast<object>());
                loans=(IList)deviceOwner.GetType().GetProperty("OriginalDeviceLoans")!.GetValue(deviceOwner)!;
            }
            Assert.Equal(2,loans.Count);
            object? exactSecond =loans[1];loans[1]=loans[0];
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);Assert.Equal(0x6D,foreignHistoryRegion.Span[0]);
            loans[1]=exactSecond;
            if(deviceHistory)
            {
                IDictionary rootDomains =(IDictionary)dependencies.GetType().GetField("Domains",Private)!.GetValue(dependencies)!;
                object pendingRoot =Assert.Single(rootDomains.Values.Cast<object>());
                PropertyInfo closeRoute =pendingRoot.GetType().GetProperty("OriginalClose")!; object closeFact =closeRoute.GetValue(pendingRoot)!;Assert.NotNull(closeFact);
                NeutralOriginalDomainCloseRequest rootCloseRequest =(NeutralOriginalDomainCloseRequest)closeFact.GetType().GetProperty("Request")!.GetValue(closeFact)!;
                INeutralOriginalDomainCloseContinuationProvider continuation =(INeutralOriginalDomainCloseContinuationProvider)model;
                Assert.Null(model.ResolveDomainClose(rootCloseRequest)); // Pure query cannot revoke restored root.
                Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
                Assert.Equal(0x6D,foreignHistoryRegion.Span[0]);Assert.Equal(0,model.ActiveDeviceLeaseCount);Assert.Equal(1,model.ActiveBindingCount);
                Assert.False(model.BindDevice(rootCloseRequest.Domain,new("device/after-root-close"),NeutralDeviceRights.Read).IsBound);
                Assert.Equal(NeutralDomainCloseDecision.Faulted,model.Close(rootCloseRequest.Domain).Decision);
                Assert.Equal(NeutralDomainCloseDecision.Faulted,continuation.ContinueOriginalDomainClose(rootCloseRequest with {InvocationId=Guid.NewGuid()}).Observation.Decision);
                Assert.False(continuation.ContinueOriginalDomainClose(rootCloseRequest with {RuntimeIncarnation=Guid.NewGuid()}).SourceFenceMatched);
                closeRoute.SetValue(pendingRoot,null);Assert.Equal(NeutralDomainCloseDecision.Faulted,continuation.ContinueOriginalDomainClose(rootCloseRequest).Observation.Decision);
                closeRoute.SetValue(pendingRoot,typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(closeFact,null));
                Assert.Equal(NeutralDomainCloseDecision.Faulted,continuation.ContinueOriginalDomainClose(rootCloseRequest).Observation.Decision);
                closeRoute.SetValue(pendingRoot,closeFact);Assert.Equal(1,model.ActiveBindingCount);
                if(fault.StartsWith("device-history-parent-",StringComparison.Ordinal))
                {
                    object parentCustodyRoot =Assert.Single(((IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_domains",Private)!.GetValue(provider)!).Values.Cast<object>());
                    PropertyInfo parentCustodyRoute =parentCustodyRoot.GetType().GetProperty("ExternalParentCloseObservation")!;
                    NeutralVirtualizationResult<NeutralExternalParentCloseReceipt>? parentCustodyRaw=null;
                    sourceSpy.BeforeSource=()=>
                    {
                        if(parentCustodyRaw is not null || parentCustodyRoute.GetValue(parentCustodyRoot) is not NeutralVirtualizationResult<NeutralExternalParentCloseReceipt> {IsSuccess:true} terminalParent)return;
                        parentCustodyRaw=terminalParent;
                        Assert.False(Monitor.IsEntered(typeof(HybridCpuPlatformAuthorityProvider).GetField("_childCreateCommitGate",Private)!.GetValue(provider)!));
                        Assert.False(kernel.TerminateProcess(owner).IsSuccess); // Reentry cannot consume the outer original.
                        parentCustodyRoute.SetValue(parentCustodyRoot,fault.EndsWith("loss",StringComparison.Ordinal)?null:terminalParent with {Value=terminalParent.Value with {NoFurtherUse=false}});
                    };
                    Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.NotNull(parentCustodyRaw);
                    Assert.Equal(0,model.ActiveBindingCount);
                    object closedExternalRoot =Assert.Single(((IDictionary)native.GetType().GetField("registry",Private)!.GetValue(native)!).Values.Cast<object>());
                    Assert.Equal("Closed",closedExternalRoot.GetType().GetProperty("Lifecycle")!.GetValue(closedExternalRoot)!.ToString());
                    Assert.Null(closedExternalRoot.GetType().GetProperty("Binding")!.GetValue(closedExternalRoot));Assert.Null(closedExternalRoot.GetType().GetProperty("Context")!.GetValue(closedExternalRoot));
                    Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                    Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);Assert.Equal(0x6D,foreignHistoryRegion.Span[0]);
                    sourceSpy.BeforeSource=null;parentCustodyRoute.SetValue(parentCustodyRoot,parentCustodyRaw);
                }
                if(fault.StartsWith("device-history-root-",StringComparison.Ordinal))
                {
                    object custodyRoot =Assert.Single(((IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_domains",Private)!.GetValue(provider)!).Values.Cast<object>());
                    PropertyInfo custodyEnvelope =custodyRoot.GetType().GetProperty("RootCloseEnvelope")!;
                    PropertyInfo custodyRequest =custodyRoot.GetType().GetProperty("RootCloseRequest")!;
                    NeutralOriginalDomainCloseObservation? custodyOriginal=null;
                    sourceSpy.BeforeSource=()=>
                    {
                        if(custodyOriginal is not null || custodyEnvelope.GetValue(custodyRoot) is not NeutralOriginalDomainCloseObservation {Observation.Decision:NeutralDomainCloseDecision.Closed} terminal)return;
                        custodyOriginal=terminal;
                        Assert.False(Monitor.IsEntered(typeof(HybridCpuPlatformAuthorityProvider).GetField("_childCreateCommitGate",Private)!.GetValue(provider)!));
                        if(fault.EndsWith("loss",StringComparison.Ordinal))custodyEnvelope.SetValue(custodyRoot,null);
                        else if(fault.EndsWith("copy",StringComparison.Ordinal))custodyEnvelope.SetValue(custodyRoot,terminal with {Observation=terminal.Observation with {}});
                        else custodyRequest.SetValue(custodyRoot,rootCloseRequest with {InvocationId=Guid.NewGuid()});
                    };
                    Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                    Assert.NotNull(custodyOriginal);Assert.Equal(0,model.ActiveBindingCount);
                    Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                    Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);Assert.Equal(0x6D,foreignHistoryRegion.Span[0]);
                    sourceSpy.BeforeSource=null;custodyRequest.SetValue(custodyRoot,rootCloseRequest);custodyEnvelope.SetValue(custodyRoot,custodyOriginal);
                }
                if(fault=="device-history-lost-reply")
                {
                    sourceSpy.LoseRootContinuationReply=true;
                    Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0,model.ActiveBindingCount);
                    Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                    Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(1,sourceSpy.RootContinuationCalls);
                    sourceSpy.LoseRootQuery=false;
                    Assert.Same(sourceSpy.OriginalRootResult,model.ResolveDomainClose(rootCloseRequest)!.Value.Observation);
                }
            }
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            if(deviceHistory){Assert.Equal(1,sourceSpy.RootCloseCalls);Assert.Equal(1,sourceSpy.RootContinuationCalls);}
            if(fault.StartsWith("device-history-parent-",StringComparison.Ordinal))
            {
                object completedParentRoot =Assert.Single(((IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_domains",Private)!.GetValue(provider)!).Values.Cast<object>());
                PlatformProviderDomainLease completedParentLease =(PlatformProviderDomainLease)completedParentRoot.GetType().GetProperty("Lease")!.GetValue(completedParentRoot)!;
                PropertyInfo completedParentRoute =completedParentRoot.GetType().GetProperty("ExternalParentCloseObservation")!;
                object? completedParentRaw =completedParentRoute.GetValue(completedParentRoot);
                Assert.True(provider.ResolveDomainClosure(completedParentLease).IsSuccess);
                completedParentRoute.SetValue(completedParentRoot,null);Assert.False(provider.ResolveDomainClosure(completedParentLease).IsSuccess);
                completedParentRoute.SetValue(completedParentRoot,completedParentRaw);Assert.True(provider.ResolveDomainClosure(completedParentLease).IsSuccess);
                Assert.Equal(1,spy.Calls["CloseOriginalDomain"]);
            }
            if(fault.StartsWith("device-history-root-",StringComparison.Ordinal))
            {
                object completedCustodyRoot =Assert.Single(((IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_domains",Private)!.GetValue(provider)!).Values.Cast<object>());
                PlatformProviderDomainLease completedCustodyLease =(PlatformProviderDomainLease)completedCustodyRoot.GetType().GetProperty("Lease")!.GetValue(completedCustodyRoot)!;
                PropertyInfo completedCustodyRoute =completedCustodyRoot.GetType().GetProperty("RootCloseEnvelope")!;
                NeutralOriginalDomainCloseObservation completedCustodyRaw =(NeutralOriginalDomainCloseObservation)completedCustodyRoute.GetValue(completedCustodyRoot)!;
                Assert.True(provider.ResolveDomainClosure(completedCustodyLease).IsSuccess);
                completedCustodyRoute.SetValue(completedCustodyRoot,completedCustodyRaw with {Observation=completedCustodyRaw.Observation with {}});
                Assert.False(provider.ResolveDomainClosure(completedCustodyLease).IsSuccess);
                completedCustodyRoute.SetValue(completedCustodyRoot,completedCustodyRaw);
                Assert.True(provider.ResolveDomainClosure(completedCustodyLease).IsSuccess);
                Assert.Equal(1,sourceSpy.RootCloseCalls);Assert.Equal(1,sourceSpy.RootContinuationCalls);
            }
            Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);Assert.Equal(0x6D,foreignHistoryRegion.Span[0]);
            Assert.Equal(2,spy.Calls["BindOriginalVirtualIo"]);Assert.Equal(2,spy.Calls["CloseOriginalVirtualIo"]);Assert.Equal(3,spy.Calls["TransferOriginalVirtualIo"]);return;
        }
        if(fault.StartsWith("loan-closure-",StringComparison.Ordinal))
        {
            OwnedBuffer<byte> foreignLoanRegion =kernel.AllocateBuffer<byte>(admin,8).Value!;foreignLoanRegion.Span[0]=0x69;
            object loanNativeRoot =Assert.Single(((IDictionary)native.GetType().GetField("registry",Private)!.GetValue(native)!).Values.Cast<object>());
            object loanBackingOwner =Assert.Single(((IEnumerable)loanNativeRoot.GetType().GetProperty("OriginalParentDevices")!.GetValue(loanNativeRoot)!).Cast<object>());
            byte[] loanBacking =(byte[])loanBackingOwner.GetType().GetField("Backing")!.GetValue(loanBackingOwner)!;loanBacking[0]=0x70;
            Assert.True(bound.IsSuccess,bound.Message);Assert.True(kernel.CloseVirtualIo(bound.Value!).IsSuccess);
            Assert.All(loanBacking,value=>Assert.Equal(0,value));Assert.Null(loanBackingOwner.GetType().GetField("Backing")!.GetValue(loanBackingOwner));
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            object dependencies =typeof(NeutralDomainRuntimeFacade).GetField("_dependencies",Private)!.GetValue(model)!;
            IDictionary loanDevices =(IDictionary)dependencies.GetType().GetField("Devices",Private)!.GetValue(dependencies)!;
            object deviceOwner =Assert.Single(loanDevices.Values.Cast<object>());
            object loanFact =Assert.Single(((IEnumerable)deviceOwner.GetType().GetProperty("OriginalDeviceLoans")!.GetValue(deviceOwner)!).Cast<object>());
            PropertyInfo closure =loanFact.GetType().GetProperty("Closure")!; object? exact =closure.GetValue(loanFact);Assert.NotNull(exact);
            closure.SetValue(loanFact,fault.EndsWith("copy",StringComparison.Ordinal)?typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(exact,null):null);
            NeutralOriginalDeviceLoan loan =(NeutralOriginalDeviceLoan)loanFact.GetType().GetProperty("Loan")!.GetValue(loanFact)!;
            Assert.False(model.RetireOriginalDeviceLoan(loan));
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
            Assert.Equal(0x69,foreignLoanRegion.Span[0]);
            closure.SetValue(loanFact,exact);Assert.True(model.RetireOriginalDeviceLoan(loan));Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
            Assert.Equal(0x69,foreignLoanRegion.Span[0]);
            Assert.Equal(1,spy.Calls["BindOriginalVirtualIo"]);Assert.Equal(1,spy.Calls["CloseOriginalVirtualIo"]);return;
        }
        if(bridgeBind)
        {
            Assert.NotNull(bridgeBindRaw);Assert.False(bound.IsSuccess);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
            Assert.Equal(0x67,publicationBacking![0]);
            bridgeSpy.BeforeSource=null;bridgeBindRoute!.SetValue(bridgeBindFact,bridgeBindRaw);
            object retained =Assert.Single(((IDictionary)typeof(RuntimeKernel).GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>());
            VirtualIoBinding retainedBinding =(VirtualIoBinding)retained.GetType().GetProperty("Binding",Private)!.GetValue(retained)!;
            Assert.True(kernel.CloseVirtualIo(retainedBinding).IsSuccess);
            Assert.All(publicationBacking,value=>Assert.Equal(0,value));
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
            Assert.Equal(1,spy.Calls["BindOriginalParentDevice"]);Assert.Equal(1,spy.Calls["BindOriginalVirtualIo"]);Assert.Equal(1,spy.Calls["CloseOriginalVirtualIo"]);return;
        }
        if(providerBind)
        {
            Assert.NotNull(providerBindRaw);Assert.False(bound.IsSuccess);
            Assert.False((bool)providerBindFact!.GetType().GetProperty("Published")!.GetValue(providerBindFact)!);
            Assert.Equal(0x65,publicationBacking![0]);Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
            sourceSpy.BeforeSource=null;providerBindRoute!.SetValue(providerBindFact,providerBindRaw);
            object retained =Assert.Single(((IDictionary)typeof(RuntimeKernel).GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>());
            VirtualIoBinding retainedBinding =(VirtualIoBinding)retained.GetType().GetProperty("Binding",Private)!.GetValue(retained)!;
            Assert.True(kernel.CloseVirtualIo(retainedBinding).IsSuccess);Assert.All(publicationBacking,value=>Assert.Equal(0,value));
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
            Assert.Equal(1,spy.Calls["BindOriginalParentDevice"]);Assert.Equal(1,spy.Calls["BindOriginalVirtualIo"]);Assert.Equal(1,spy.Calls["ContainOriginalVirtualIoBacking"]);return;
        }
        if(bindPublication)
        {
            Assert.NotNull(publicationRaw);Assert.False(bound.IsSuccess);
            Assert.False((bool)publicationFact!.GetType().GetField("Published")!.GetValue(publicationFact)!);
            Assert.Equal(0x63,publicationBacking![0]);Assert.Equal(1,spy.Calls["BindOriginalParentDevice"]);Assert.Equal(1,spy.Calls["BindOriginalVirtualIo"]);
            rootSpy.BeforeQuery=null;publicationRoute!.SetValue(publicationFact,publicationRaw);
            object retained =Assert.Single(((IDictionary)typeof(RuntimeKernel).GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>());
            VirtualIoBinding retainedBinding =(VirtualIoBinding)retained.GetType().GetProperty("Binding",Private)!.GetValue(retained)!;
            Assert.True(kernel.CloseVirtualIo(retainedBinding).IsSuccess);
            Assert.All(publicationBacking,value=>Assert.Equal(0,value));
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
            Assert.Equal(1,spy.Calls["ContainOriginalVirtualIoBacking"]);return;
        }
        if (sdkCleanup || providerCleanup || fault.StartsWith("bind-bridge-cleanup-",StringComparison.Ordinal) || fault.StartsWith("bind-duplicate-", StringComparison.Ordinal) || fault.StartsWith("refused", StringComparison.Ordinal) || fault is "io-bind-copy" or "device-bind-null" or "device-bind-copy" or "io-bind-query-throw" or "bind-containment-lost" or "bind-containment-query-throw" or "bind-containment-copy" or "bind-history-loss" or "bind-charge-route-loss" or "bind-bridge-history-loss" or "bind-containment-aba")
        {
            Assert.False(bound.IsSuccess);
            Assert.False(kernel.BindVirtualIo(owner, domain.Domain, device, new(PlatformDeviceRights.Read, 512)).IsSuccess);
            object unknownLocal = Assert.Single(((IDictionary)typeof(RuntimeKernel).GetField("_virtualIo", Private)!.GetValue(kernel)!).Values.Cast<object>());
            VirtualIoBinding unknownBinding = (VirtualIoBinding)unknownLocal.GetType().GetProperty("Binding", Private)!.GetValue(unknownLocal)!;
            PlatformOriginalVirtualIoBindRequest unknownRequest = (PlatformOriginalVirtualIoBindRequest)unknownLocal.GetType().GetProperty("OriginalBindRequest", Private)!.GetValue(unknownLocal)!;
            Assert.False(provider.CleanupOriginalVirtualIoBind(unknownRequest with { }).IsSuccess);
            Assert.Null(provider.ResolveOriginalVirtualIoBindCleanup(unknownRequest with { }));
            IDictionary owners = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderVirtualIo", Private)!.GetValue(provider)!;
            object original = Assert.Single(owners.Values.Cast<object>());
            NeutralOriginalVirtualIoRequest neutral = (NeutralOriginalVirtualIoRequest)original.GetType().GetProperty("NativeOriginalRequest")!.GetValue(original)!;
            Assert.False(adapter.CleanupOriginalVirtualIoBind(neutral with { }).IsSuccess);
            IDictionary unknownDevices = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_deviceLeases", Private)!.GetValue(provider)!;
            NeutralDeviceLease unknownDevice = (NeutralDeviceLease)Assert.Single(unknownDevices.Values.Cast<object>()).GetType().GetProperty("HybridCpuLease")!.GetValue(Assert.Single(unknownDevices.Values.Cast<object>()))!;
            NeutralDomainRuntimeFacade unknownForeign = new NeutralDomainRuntimeFacade(); NeutralDomainBindingLease unknownForeignRoot = unknownForeign.Bind(NeutralDomainProfile.OrdinaryService).Lease;
            NeutralDeviceLease unknownForeignDevice = unknownForeign.BindDevice(unknownForeignRoot, unknownDevice.Device, unknownDevice.Rights).Lease;
            Assert.Equal(unknownDevice, unknownForeignDevice);
            Assert.NotEqual(NeutralDeviceCloseDecision.Closed, model.CloseDevice(unknownDevice).Decision);
            if (fault == "refused-op-exhausted") typeof(HybridCpuExecutableChildAdapter).GetField("nextOperation", Private)!.SetValue(adapter, ulong.MaxValue);
            if (fault == "bind-bridge-history-loss")
            {
                object bridgeDevice = Assert.Single(((IDictionary)typeof(PlatformAuthorityBridge).GetField("_originalDeviceOwners", Private)!.GetValue(kernel.PlatformAuthority)!).Values.Cast<object>());
                ((IList)bridgeDevice.GetType().GetProperty("VirtualIoBindInvocations")!.GetValue(bridgeDevice)!).Clear();
            }
            PropertyInfo quantumRoute = unknownLocal.GetType().GetProperty("BackingChargeOwner", Private)!;
            object? quantumOwner = quantumRoute.GetValue(unknownLocal);
            object? sdkFact=null,sdkRaw=null;FieldInfo? sdkRoute=null;
            byte[]? sdkBacking=null;object? sdkBackingOwner=null;
            if(sdkCleanup)
            {
                sdkFact=Assert.Single(((IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("originalIo",Private)!.GetValue(adapter)!).Values.Cast<object>());
                bool noEffect =fault.StartsWith("refused",StringComparison.Ordinal);
                if(!noEffect)
                {
                    object sdkNativeRoot =Assert.Single(((IDictionary)native.GetType().GetField("registry",Private)!.GetValue(native)!).Values.Cast<object>());
                    sdkBackingOwner=Assert.Single(((IEnumerable)sdkNativeRoot.GetType().GetProperty("OriginalParentDevices")!.GetValue(sdkNativeRoot)!).Cast<object>());
                    sdkBacking=(byte[])sdkBackingOwner.GetType().GetField("Backing")!.GetValue(sdkBackingOwner)!;
                    Assert.Equal(512,sdkBacking.Length);sdkBacking[0]=0x61;
                }
                rootSpy.BeforeQuery=()=>
                {
                    if(sdkRaw is not null || !(bool)sdkFact.GetType().GetField(noEffect?"DeviceNoEffectValidated":"ContainmentValidated")!.GetValue(sdkFact)!)return;
                    sdkRoute=sdkFact.GetType().GetField(noEffect?"DeviceNoEffect":"Containment")!;sdkRaw=sdkRoute.GetValue(sdkFact);Assert.NotNull(sdkRaw);
                    Assert.False(Monitor.IsEntered(typeof(HybridCpuExecutableChildAdapter).GetField("sync",Private)!.GetValue(adapter)!));
                    Assert.False(kernel.CloseVirtualIo(unknownBinding).IsSuccess);
                    if(fault.EndsWith("throw",StringComparison.Ordinal))throw new IOException("Last SDK cleanup freshness callback lost.");
                    sdkRoute.SetValue(sdkFact,fault.EndsWith("copy",StringComparison.Ordinal)?typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(sdkRaw,null):null);
                };
            }
            object? cleanupFact=null,cleanupRaw=null;PropertyInfo? cleanupRoute=null;byte[]? cleanupBacking=null;
            if(fault.StartsWith("bind-bridge-cleanup-",StringComparison.Ordinal))
            {
                object nativeRoot =Assert.Single(((IDictionary)native.GetType().GetField("registry",Private)!.GetValue(native)!).Values.Cast<object>());
                object deviceOwner =Assert.Single(((IEnumerable)nativeRoot.GetType().GetProperty("OriginalParentDevices")!.GetValue(nativeRoot)!).Cast<object>());
                cleanupBacking=(byte[])deviceOwner.GetType().GetField("Backing")!.GetValue(deviceOwner)!;Assert.Equal(512,cleanupBacking.Length);
                kernel.BeforeVirtualIoClosureLocalCommit=()=>
                {
                    kernel.BeforeVirtualIoClosureLocalCommit=null;
                    Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate",Private)!.GetValue(kernel.PlatformAuthority)!));
                    object bridgeDevice =Assert.Single(((IDictionary)typeof(PlatformAuthorityBridge).GetField("_originalDeviceOwners",Private)!.GetValue(kernel.PlatformAuthority)!).Values.Cast<object>());
                    cleanupFact=Assert.Single(((IEnumerable)bridgeDevice.GetType().GetProperty("VirtualIoBindInvocations")!.GetValue(bridgeDevice)!).Cast<object>());
                    cleanupRoute=cleanupFact.GetType().GetProperty("CleanupRaw")!;cleanupRaw=cleanupRoute.GetValue(cleanupFact);Assert.NotNull(cleanupRaw);
                    PlatformAuthorityResult<PlatformOriginalVirtualIoBindCleanupClosure> raw =(PlatformAuthorityResult<PlatformOriginalVirtualIoBindCleanupClosure>)cleanupRaw!;
                    cleanupRoute.SetValue(cleanupFact,fault.EndsWith("copy",StringComparison.Ordinal)?raw with {Value=raw.Value! with { }}:null);
                    Assert.False(kernel.CloseVirtualIo(unknownBinding).IsSuccess);
                };
            }
            if (fault == "bind-charge-route-loss") kernel.BeforeVirtualIoClosureLocalCommit = () =>
            { kernel.BeforeVirtualIoClosureLocalCommit = null; quantumRoute.SetValue(unknownLocal, null); };
            spy.After = name =>
            {
                Assert.False(Monitor.IsEntered(typeof(HybridCpuExecutableChildAdapter).GetField("sync", Private)!.GetValue(adapter)!));
                Assert.False(Monitor.IsEntered(typeof(HybridCpuPlatformAuthorityProvider).GetField("_childCreateCommitGate", Private)!.GetValue(provider)!));
                Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", Private)!.GetValue(kernel.PlatformAuthority)!));
                if (name == "ContainOriginalVirtualIoBacking") Assert.False(kernel.CloseVirtualIo(unknownBinding).IsSuccess);
                if (fault == "bind-containment-aba" && name == "ResolveOriginalVirtualIoBackingContainment")
                {
                    object child = Assert.Single(((IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("children", Private)!.GetValue(adapter)!).Values.Cast<object>());
                    PropertyInfo state = child.GetType().GetProperty("State")!; state.SetValue(child, state.GetValue(child));
                }
            };
            if (fault == "device-bind-null")
            {
                KernelResult terminatedUnknown = kernel.TerminateProcess(owner); Assert.True(terminatedUnknown.IsSuccess, terminatedUnknown.Message);
                spy.After = null;
                Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
                Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
                Assert.Equal(0, model.ActiveDeviceLeaseCount); Assert.Equal(1, unknownForeign.ActiveDeviceLeaseCount);
                Assert.Equal(1, spy.Calls["BindOriginalParentDevice"]); Assert.Equal(1, spy.Calls["ContainOriginalVirtualIoBacking"]);
                Assert.True(kernel.CloseVirtualIo(unknownBinding).IsSuccess);
                Assert.True(provider.CleanupOriginalVirtualIoBind(unknownRequest).IsSuccess);
                Assert.True(adapter.CleanupOriginalVirtualIoBind(neutral).IsSuccess);
                return;
            }
            if(providerCleanup)
            {
                PlatformAuthorityResult<PlatformOriginalVirtualIoBindCleanupClosure> originalReply =provider.CleanupOriginalVirtualIoBind(unknownRequest);Assert.True(originalReply.IsSuccess,originalReply.Message);
                Assert.Equal(1024UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                foreach(string? name in new[]{"BindCleanup","BindCleanupRaw"})
                {
                    PropertyInfo output =original.GetType().GetProperty(name)!; object? exact =output.GetValue(original);Assert.NotNull(exact);
                    object? copy=null;
                    if(fault.EndsWith("copy",StringComparison.Ordinal))copy=name=="BindCleanup"?originalReply.Value! with { }:
                        ((NeutralVirtualizationResult<NeutralOriginalVirtualIoBindCleanupClosure>)exact!) with {Value=((NeutralVirtualizationResult<NeutralOriginalVirtualIoBindCleanupClosure>)exact!).Value with { }};
                    output.SetValue(original,copy);
                    Assert.Null(provider.ResolveOriginalVirtualIoBindCleanup(unknownRequest));
                    Assert.False(provider.CleanupOriginalVirtualIoBind(unknownRequest).IsSuccess);
                    Assert.False(kernel.CloseVirtualIo(unknownBinding).IsSuccess);
                    Assert.Equal(1024UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                    Assert.Equal(1,unknownForeign.ActiveDeviceLeaseCount);Assert.Equal(1,spy.Calls["ContainOriginalVirtualIoBacking"]);
                    output.SetValue(original,exact);
                }
            }
            KernelResult unknownClosed = kernel.CloseVirtualIo(unknownBinding);
            if(sdkCleanup)
            {
                Assert.NotNull(sdkRaw);
                Assert.Equal((false,1024UL,false),(unknownClosed.IsSuccess,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used,(bool)sdkFact!.GetType().GetField("Consumed")!.GetValue(sdkFact)!));
                Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
                Assert.Equal(1,unknownForeign.ActiveDeviceLeaseCount);
                if(sdkBacking is not null)
                {
                    Assert.All(sdkBacking,value=>Assert.Equal(0,value));
                    Assert.Null(sdkBackingOwner!.GetType().GetField("Backing")!.GetValue(sdkBackingOwner));
                }
                Assert.Equal(fault.StartsWith("refused",StringComparison.Ordinal)?0:1,spy.Calls.GetValueOrDefault("ContainOriginalVirtualIoBacking"));
                rootSpy.BeforeQuery=null;sdkRoute!.SetValue(sdkFact,sdkRaw);unknownClosed=kernel.CloseVirtualIo(unknownBinding);
                Assert.True(unknownClosed.IsSuccess,unknownClosed.Message);
                foreach(FieldInfo? field in new[]{sdkRoute,sdkFact!.GetType().GetField("BindCleanup")!})
                {
                    object? exact =field.GetValue(sdkFact);Assert.NotNull(exact);
                    field.SetValue(sdkFact,fault.EndsWith("copy",StringComparison.Ordinal)?typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(exact,null):null);
                    Assert.Null(adapter.ResolveOriginalVirtualIoBindCleanup(neutral));
                    Assert.False(adapter.CleanupOriginalVirtualIoBind(neutral).IsSuccess);
                    field.SetValue(sdkFact,exact);Assert.True(adapter.CleanupOriginalVirtualIoBind(neutral).IsSuccess);
                }
            }
            if(fault.StartsWith("bind-bridge-cleanup-",StringComparison.Ordinal))
            {
                Assert.NotNull(cleanupRaw);Assert.False(unknownClosed.IsSuccess);
                Assert.Equal(1024UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
                Assert.All(cleanupBacking!,value=>Assert.Equal(0,value));Assert.Equal(1,unknownForeign.ActiveDeviceLeaseCount);
                Assert.False(kernel.CloseVirtualIo(unknownBinding).IsSuccess);Assert.Equal(1,spy.Calls["ContainOriginalVirtualIoBacking"]);
                cleanupRoute!.SetValue(cleanupFact,cleanupRaw);unknownClosed=kernel.CloseVirtualIo(unknownBinding);
                Assert.True(unknownClosed.IsSuccess,unknownClosed.Message);
                PlatformAuthorityResult<PlatformOriginalVirtualIoBindCleanupClosure> raw =(PlatformAuthorityResult<PlatformOriginalVirtualIoBindCleanupClosure>)cleanupRaw!;
                cleanupRoute.SetValue(cleanupFact,fault.EndsWith("copy",StringComparison.Ordinal)?raw with {Value=raw.Value! with { }}:null);
                Assert.False(kernel.PlatformAuthority.CleanupOriginalVirtualIoBind(unknownRequest,commit=>commit(KernelResult.Ok)).IsSuccess);
                Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                cleanupRoute.SetValue(cleanupFact,cleanupRaw);
                Assert.True(kernel.PlatformAuthority.CleanupOriginalVirtualIoBind(unknownRequest,commit=>commit(KernelResult.Ok)).IsSuccess);
            }
            if (fault is "refused-copy" or "refused-query-throw")
            {
                Assert.False(unknownClosed.IsSuccess);
                Assert.Equal(1024UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
                Assert.Equal(0, spy.Calls.GetValueOrDefault("ContainOriginalVirtualIoBacking"));
                if (fault == "refused-query-throw") { spy.Fault = "exact"; unknownClosed = kernel.CloseVirtualIo(unknownBinding); }
                else { Assert.False(kernel.CloseVirtualIo(unknownBinding).IsSuccess); Assert.False(kernel.TerminateProcess(owner).IsSuccess); return; }
            }
            if (fault.StartsWith("bind-duplicate-", StringComparison.Ordinal) || fault is "bind-containment-query-throw" or "bind-containment-copy" or "bind-history-loss" or "bind-charge-route-loss" or "bind-bridge-history-loss" or "bind-containment-aba")
            {
                Assert.False(unknownClosed.IsSuccess);
                Assert.Equal(1024UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
                Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
                Assert.Equal(fault == "bind-bridge-history-loss" ? 0 : 1, spy.Calls.GetValueOrDefault("ContainOriginalVirtualIoBacking"));
                if (fault == "bind-containment-query-throw") { spy.Fault = "exact"; unknownClosed = kernel.CloseVirtualIo(unknownBinding); }
                else if (fault == "bind-charge-route-loss") { quantumRoute.SetValue(unknownLocal, quantumOwner); unknownClosed = kernel.CloseVirtualIo(unknownBinding); }
                else
                {
                    Assert.False(kernel.CloseVirtualIo(unknownBinding).IsSuccess);
                    Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                    Assert.Equal(fault == "bind-bridge-history-loss" ? 0 : 1, spy.Calls.GetValueOrDefault("ContainOriginalVirtualIoBacking"));
                    if (fault.StartsWith("bind-duplicate-", StringComparison.Ordinal))
                    {
                        object nativeRoot = Assert.Single(((IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(native)!).Values.Cast<object>());
                        object backingOwner = ((IList)nativeRoot.GetType().GetProperty("OriginalParentDevices")!.GetValue(nativeRoot)!)[0]!;
                        byte[] backing = (byte[])backingOwner.GetType().GetField("Backing")!.GetValue(backingOwner)!;
                        Assert.Equal(512, backing.Length); Assert.Equal(17, backing[0]);
                        Assert.Equal(1, spy.Calls["BindOriginalParentDevice"]); Assert.Equal(1, spy.Calls["BindOriginalVirtualIo"]);
                        Assert.Equal(1, model.ActiveDeviceLeaseCount);
                    }
                    Assert.Equal(1, unknownForeign.ActiveDeviceLeaseCount); return;
                }
            }
            Assert.True(unknownClosed.IsSuccess, unknownClosed.Message); spy.After = null;
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            if (fault == "refused-op-exhausted")
            {
                Assert.True(provider.CleanupOriginalVirtualIoBind(unknownRequest).IsSuccess);
                Assert.True(adapter.CleanupOriginalVirtualIoBind(neutral).IsSuccess);
                Assert.True(provider.ResolveOriginalVirtualIoBindCleanup(unknownRequest)!.NoEffect);
                Assert.False(kernel.TerminateProcess(owner).IsSuccess); // Independent child Close still needs an unexhausted operation identity.
                Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
                Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
                Assert.Equal(0, spy.Calls.GetValueOrDefault("ContainOriginalVirtualIoBacking")); return;
            }
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(0, model.ActiveDeviceLeaseCount); Assert.Equal(1, unknownForeign.ActiveDeviceLeaseCount);
            Assert.Equal(1, spy.Calls["BindOriginalParentDevice"]); Assert.Equal(fault.StartsWith("refused", StringComparison.Ordinal) ? 0 : 1, spy.Calls.GetValueOrDefault("ContainOriginalVirtualIoBacking"));
            Assert.Equal(fault.StartsWith("refused", StringComparison.Ordinal), provider.ResolveOriginalVirtualIoBindCleanup(unknownRequest)!.NoEffect);
            Assert.True(provider.CleanupOriginalVirtualIoBind(unknownRequest).IsSuccess);
            Assert.True(adapter.CleanupOriginalVirtualIoBind(neutral).IsSuccess);
            Assert.False(provider.BindOriginalVirtualIo(unknownRequest).IsSuccess);
            Assert.False(adapter.BindOriginalVirtualIo(neutral).IsSuccess);
            return;
        }
        if (fault == "source-aba")
        {
            Assert.False(bound.IsSuccess); Assert.False(kernel.BindVirtualIo(owner, domain.Domain, device, new(PlatformDeviceRights.Read, 512)).IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess); Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1, spy.Calls["BindOriginalVirtualIo"]); Assert.Equal(1, model.ActiveDeviceLeaseCount);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(1024UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used); return;
        }
        Assert.True(bound.IsSuccess, bound.Message);
        IDictionary facts = (IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("originalIo", Private)!.GetValue(adapter)!;
        object fact = Assert.Single(facts.Values.Cast<object>());
        ExternalOriginalVirtualIoRequest request = (ExternalOriginalVirtualIoRequest)fact.GetType().GetField("NativeRequest")!.GetValue(fact)!;
        Assert.True(native.TransferOriginalVirtualIo(request, 0, [17, 19], true));
        byte[] read = new byte[2]; Assert.True(native.TransferOriginalVirtualIo(request, 0, read, false)); Assert.Equal(new byte[] { 17, 19 }, read);
        NeutralOriginalVirtualIoTransferReceipt? adapterRead = null;
        PlatformOriginalVirtualIoTransferReceipt? providerRead = null;
        if(fault == "exact")
        {
            NeutralVirtualIoLease lease = (NeutralVirtualIoLease)fact.GetType().GetField("Lease")!.GetValue(fact)!;
            NeutralOriginalVirtualIoTransferRequest transfer = new NeutralOriginalVirtualIoTransferRequest(lease,0,true,2,new byte[]{23,29});
            spy.Fault = "transfer-reply-lost";
            NeutralVirtualizationResult<NeutralOriginalVirtualIoTransferReceipt> written = adapter.TransferOriginalVirtualIo(transfer); Assert.True(written.IsSuccess,written.Reason);
            Assert.Same(written.Value,adapter.ResolveOriginalVirtualIoTransfer(transfer).Value);
            Assert.Same(written.Value,adapter.TransferOriginalVirtualIo(transfer).Value);
            spy.Fault = "exact";
            NeutralOriginalVirtualIoTransferRequest readTransfer = new NeutralOriginalVirtualIoTransferRequest(lease,0,false,2);
            NeutralVirtualizationResult<NeutralOriginalVirtualIoTransferReceipt> observed = adapter.TransferOriginalVirtualIo(readTransfer); Assert.True(observed.IsSuccess,observed.Reason);
            adapterRead = observed.Value;
            Assert.Equal(new byte[]{23,29},adapterRead!.CopyReadData());
            Assert.Equal(2,spy.Calls["TransferOriginalVirtualIo"]);
            object providerIo = Assert.Single(((IDictionary)provider.GetType().GetField("_originalProviderVirtualIo",Private)!.GetValue(provider)!).Values.Cast<object>());
            PlatformProviderVirtualIoLease providerLease = (PlatformProviderVirtualIoLease)providerIo.GetType().GetProperty("Lease")!.GetValue(providerIo)!;
            PlatformOriginalVirtualIoTransferRequest providerTransfer = new PlatformOriginalVirtualIoTransferRequest(providerLease,4,true,2,new byte[]{31,37});
            spy.Fault = "transfer-reply-lost";
            PlatformAuthorityResult<PlatformOriginalVirtualIoTransferReceipt> sent = provider.TransferOriginalVirtualIo(providerTransfer); Assert.True(sent.IsSuccess,sent.Message);
            Assert.Same(sent.Value,provider.ResolveOriginalVirtualIoTransfer(providerTransfer).Value);
            Assert.Same(sent.Value,provider.TransferOriginalVirtualIo(providerTransfer).Value);
            spy.Fault = "exact";
            PlatformAuthorityResult<PlatformOriginalVirtualIoTransferReceipt> received = provider.TransferOriginalVirtualIo(new PlatformOriginalVirtualIoTransferRequest(providerLease,4,false,2));
            Assert.True(received.IsSuccess,received.Message); providerRead = received.Value;
            Assert.Equal(new byte[]{31,37},providerRead!.CopyReadData());
            Assert.Equal(4,spy.Calls["TransferOriginalVirtualIo"]);
            object localIo = Assert.Single(((IDictionary)kernel.GetType().GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>());
            PlatformVirtualIoBinding platformIo = (PlatformVirtualIoBinding)localIo.GetType().GetProperty("Platform",Private)!.GetValue(localIo)!;
            KernelResult<PlatformOriginalVirtualIoTransferRequest> preparedWrite = kernel.PlatformAuthority.PrepareVirtualIoTransfer(platformIo,8,true,2,new byte[]{41,43});
            Assert.True(preparedWrite.IsSuccess,preparedWrite.Message);
            spy.Fault = "transfer-reply-lost";
            KernelResult<PlatformOriginalVirtualIoTransferReceipt> bridgeWrite = kernel.PlatformAuthority.InvokeVirtualIoTransfer(platformIo,preparedWrite.Value!);
            Assert.True(bridgeWrite.IsSuccess,bridgeWrite.Message);
            Assert.Same(bridgeWrite.Value,kernel.PlatformAuthority.InvokeVirtualIoTransfer(platformIo,preparedWrite.Value!).Value);
            spy.Fault = "exact";
            Assert.Equal(5,spy.Calls["TransferOriginalVirtualIo"]);
            byte[] bridgeBytes = new byte[2]; Assert.True(native.TransferOriginalVirtualIo(request,8,bridgeBytes,false));
            Assert.Equal(new byte[]{41,43},bridgeBytes);
            Assert.False(kernel.TransferVirtualIo(owner,new KernelVirtualIoTransferRequest(bound.Value!,0,true,4097,new byte[4097])).IsSuccess);
            Assert.False(kernel.TransferVirtualIo(owner,new KernelVirtualIoTransferRequest(bound.Value!,0,false,2,new byte[2])).IsSuccess);
            KernelVirtualIoTransferRequest kernelWrite = new KernelVirtualIoTransferRequest(bound.Value!,12,true,2,new byte[]{47,53});
            spy.Fault="transfer-reply-lost";
            KernelResult<PlatformOriginalVirtualIoTransferReceipt> kernelSent =kernel.TransferVirtualIo(owner,kernelWrite); Assert.True(kernelSent.IsSuccess,kernelSent.Message);
            Assert.Same(kernelSent.Value,kernel.TransferVirtualIo(owner,kernelWrite).Value);
            spy.Fault="exact";
            KernelVirtualIoTransferRequest kernelReadRequest =new KernelVirtualIoTransferRequest(bound.Value!,12,false,2);
            KernelResult<PlatformOriginalVirtualIoTransferReceipt> kernelRead =kernel.TransferVirtualIo(owner,kernelReadRequest); Assert.True(kernelRead.IsSuccess,kernelRead.Message);
            Assert.Equal(new byte[]{47,53},kernelRead.Value!.CopyReadData());
            Assert.Same(kernelRead.Value,kernel.TransferVirtualIo(owner,kernelReadRequest).Value);
            Assert.Equal(1026UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(7,spy.Calls["TransferOriginalVirtualIo"]);
            ProcessHandle service =TestFixtures.Create(kernel,105,1005).Handle;
            ProtocolDefinitionV1 protocol =IVirtualizationServiceProtocol.CreateDefinition();
            ServiceContractIdentity contract =new ServiceContractIdentity(protocol.ContractName,"1",protocol.ContractDigest);
            ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"actual-io-data",contract,protocol,IVirtualizationServiceResponseProtocol.Definition,[]).Value!;
            EndpointSessionHandle session =kernel.OpenSession(owner,descriptor,[]).Value;
            RuntimeVirtualizationServiceHost serviceHost =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
            IVirtualizationService client =IVirtualizationServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
            Task<VirtualIoTransferResponse> writeTask =client.TransferIoAsync(new(bound.Value!.Id,bound.Value.Generation,16,true,2,new BoundedBytes(new byte[]{59,61}))).AsTask();
            Assert.True(serviceHost.ProcessNext().IsSuccess);Assert.True(writeTask.GetAwaiter().GetResult().Completed);
            Task<VirtualIoTransferResponse> readTask =client.TransferIoAsync(new(bound.Value.Id,bound.Value.Generation,16,false,2,default)).AsTask();
            Assert.True(serviceHost.ProcessNext().IsSuccess);Assert.Equal(new byte[]{59,61},readTask.GetAwaiter().GetResult().Data.ToArray());
            Assert.Equal(1028UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(9,spy.Calls["TransferOriginalVirtualIo"]);
            Assert.True(kernel.CloseSession(owner,session).IsSuccess);
        }
        IDictionary devices = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_deviceLeases", Private)!.GetValue(provider)!;
        object providerDevice = Assert.Single(devices.Values.Cast<object>());
        NeutralDeviceLease originalDevice = (NeutralDeviceLease)providerDevice.GetType().GetProperty("HybridCpuLease")!.GetValue(providerDevice)!;
        NeutralDomainRuntimeFacade foreign = new NeutralDomainRuntimeFacade(); NeutralDomainBindingLease foreignRoot = foreign.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        NeutralDeviceLease foreignDevice = foreign.BindDevice(foreignRoot, originalDevice.Device, originalDevice.Rights).Lease;
        Assert.Equal(originalDevice, foreignDevice);
        Assert.NotEqual(NeutralDeviceCloseDecision.Closed, model.CloseDevice(originalDevice).Decision); // Existing native owner sees the actual external borrower.
        if(fault.StartsWith("kernel-read-",StringComparison.Ordinal))
        {
            KernelVirtualIoTransferRequest transfer = new KernelVirtualIoTransferRequest(bound.Value!,0,false,2);
            if(fault=="kernel-read-capacity")
            {
                Assert.False(kernel.TransferVirtualIo(owner,transfer).IsSuccess);
                Assert.False(kernel.TransferVirtualIo(owner,transfer).IsSuccess);
                Assert.Equal(0,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
                Assert.Equal(1024UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                Assert.True(kernel.CloseVirtualIo(bound.Value!).IsSuccess);Assert.True(kernel.TerminateProcess(owner).IsSuccess);
                Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                Assert.Equal(1,foreign.ActiveDeviceLeaseCount);return;
            }
            if(fault is "kernel-read-model-incarnation" or "kernel-read-adapter-copy" or "kernel-read-provider-copy" or "kernel-read-native-generation" or "kernel-read-adapter-revision" or "kernel-read-source-revision" or "kernel-read-history" or "kernel-read-fact-copy")
            {
                object originalIo =Assert.Single(((IDictionary)kernel.GetType().GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>());
                IList? replacedHistory=null;object? originalTransferFact=null;
                Guid originalIncarnation =model.MappingClosureIncarnation;
                spy.After=name=>
                {
                    if(name!="TransferOriginalVirtualIo")return;
                    Assert.False(kernel.CloseVirtualIo(bound.Value!).IsSuccess);
                    if(fault=="kernel-read-model-incarnation")typeof(NeutralDomainRuntimeFacade).GetField("<MappingClosureIncarnation>k__BackingField",Private)!.SetValue(model,Guid.NewGuid());
                    else if(fault is "kernel-read-adapter-copy" or "kernel-read-provider-copy")
                    {
                        object historyOwner = fault=="kernel-read-adapter-copy" ? Assert.Single(((IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("originalIo",Private)!.GetValue(adapter)!).Values.Cast<object>()) : Assert.Single(((IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderVirtualIo",Private)!.GetValue(provider)!).Values.Cast<object>());
                        IList history =(IList)historyOwner.GetType().GetProperty("Transfers")!.GetValue(historyOwner)!;
                        replacedHistory=history;originalTransferFact=history[0];
                        history[0]=typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(history[0],null);
                    }
                    else if(fault=="kernel-read-native-generation")rootSpy.FeatureGenerationOverride=native.QueryFeatures().Generation+1;
                    else if(fault=="kernel-read-adapter-revision")
                    {
                        object child =Assert.Single(((IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("children",Private)!.GetValue(adapter)!).Values.Cast<object>());
                        PropertyInfo state =child.GetType().GetProperty("State")!;state.SetValue(child,state.GetValue(child));
                    }
                    else if(fault=="kernel-read-source-revision")
                    {
                        PropertyInfo state =originalIo.GetType().GetProperty("State",Private)!;
                        state.SetValue(originalIo,state.GetValue(originalIo));
                    }
                    else
                    {
                        IList history =(IList)originalIo.GetType().GetProperty("Transfers",Private)!.GetValue(originalIo)!;
                        if(fault=="kernel-read-history")history.Clear();
                        else history[0]=typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(history[0],null);
                    }
                };
                Assert.False(kernel.TransferVirtualIo(owner,transfer).IsSuccess);
                spy.After=null;
                Assert.False(kernel.TransferVirtualIo(owner,transfer).IsSuccess);
                if(fault=="kernel-read-native-generation"){rootSpy.FeatureGenerationOverride=null;Assert.False(kernel.TransferVirtualIo(owner,transfer).IsSuccess);}
                if(fault=="kernel-read-model-incarnation"){typeof(NeutralDomainRuntimeFacade).GetField("<MappingClosureIncarnation>k__BackingField",Private)!.SetValue(model,originalIncarnation);Assert.False(kernel.TransferVirtualIo(owner,transfer).IsSuccess);}
                if(replacedHistory is not null){replacedHistory[0]=originalTransferFact;Assert.False(kernel.TransferVirtualIo(owner,transfer).IsSuccess);}
                if(fault=="kernel-read-source-revision")
                {
                    object transferOwner =Assert.Single(((IList)originalIo.GetType().GetProperty("Transfers",Private)!.GetValue(originalIo)!).Cast<object>());
                    object? captured =transferOwner.GetType().GetField("Revision",Private)!.GetValue(transferOwner);
                    originalIo.GetType().GetProperty("SourceRevision",Private)!.SetValue(originalIo,captured);
                    Assert.False(kernel.TransferVirtualIo(owner,transfer).IsSuccess); // Equal projection cannot undo observed source loss.
                }
                Assert.Equal(1,spy.Calls["TransferOriginalVirtualIo"]);
                Assert.Equal(1026UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                KernelResult close =kernel.CloseVirtualIo(bound.Value!);
                if(fault is "kernel-read-history" or "kernel-read-fact-copy")
                {
                    Assert.False(close.IsSuccess);Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                    Assert.Equal(1026UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                    Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
                }
                else
                {
                    Assert.True(close.IsSuccess,close.Message);Assert.True(kernel.TerminateProcess(owner).IsSuccess);
                    Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                    Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
                }
                Assert.Equal(1,foreign.ActiveDeviceLeaseCount);return;
            }
            spy.Fault="kernel-read-query-loss";
            Assert.False(kernel.TransferVirtualIo(owner,transfer).IsSuccess);
            Assert.False(kernel.TransferVirtualIo(owner,transfer).IsSuccess);
            Assert.Equal(1,spy.Calls["TransferOriginalVirtualIo"]);
            Assert.Equal(1026UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            spy.Fault="exact";
            KernelResult<PlatformOriginalVirtualIoTransferReceipt> resolved =kernel.TransferVirtualIo(owner,transfer); Assert.True(resolved.IsSuccess,resolved.Message);
            Assert.Equal(new byte[]{17,19},resolved.Value!.CopyReadData());
            Assert.Equal(1,spy.Calls["TransferOriginalVirtualIo"]);
            object localIo =Assert.Single(((IDictionary)kernel.GetType().GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>());
            object transferFact =Assert.Single(((IList)localIo.GetType().GetProperty("Transfers",Private)!.GetValue(localIo)!).Cast<object>());
            FieldInfo chargeField =transferFact.GetType().GetField("ChargeOwner",Private)!;
            object? snapshotCharge =chargeField.GetValue(transferFact);
            if(fault=="kernel-read-charge-route")
            {
                kernel.BeforeVirtualIoClosureLocalCommit=()=>{kernel.BeforeVirtualIoClosureLocalCommit=null;chargeField.SetValue(transferFact,null);};
                Assert.False(kernel.CloseVirtualIo(bound.Value!).IsSuccess);
                Assert.Equal(1026UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
                chargeField.SetValue(transferFact,snapshotCharge);
            }
            Assert.True(kernel.CloseVirtualIo(bound.Value!).IsSuccess);
            Assert.Throws<InvalidOperationException>(()=>resolved.Value!.CopyReadData());
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
            Assert.Equal(1,foreign.ActiveDeviceLeaseCount);
            Assert.Equal(1,spy.Calls["TransferOriginalVirtualIo"]);Assert.Equal(1,spy.Calls["CloseOriginalVirtualIo"]);
            return;
        }
        if (fault.StartsWith("duplicate-", StringComparison.Ordinal))
        {
            IDictionary rootInventory = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(native)!;
            object nativeRoot = Assert.Single(rootInventory.Values.Cast<object>());
            IList nativeDevices = (IList)nativeRoot.GetType().GetProperty("OriginalParentDevices")!.GetValue(nativeRoot)!;
            object nativeDevice = Assert.Single(nativeDevices.Cast<object>());
            byte[] backing = (byte[])nativeDevice.GetType().GetField("Backing")!.GetValue(nativeDevice)!;
            object historyOwner = nativeDevice;
            string historyName = "Users", issuedName = "UsersIssued";
            if (fault == "duplicate-device") { historyOwner = nativeRoot; historyName = "OriginalParentDevices"; issuedName = "OriginalParentDevicesIssued"; }
            if (fault == "duplicate-child")
            {
                historyOwner = Assert.Single(((IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(native)!).Values.Cast<object>());
                historyName = "OriginalVirtualIo"; issuedName = "OriginalVirtualIoIssued";
            }
            IList history = (IList)historyOwner.GetType().GetProperty(historyName)!.GetValue(historyOwner)!;
            history.Add(history[0]);
            FieldInfo? issuedField = historyOwner.GetType().GetField(issuedName);
            if (issuedField is not null) issuedField.SetValue(historyOwner, (ulong)history.Count);
            else historyOwner.GetType().GetProperty(issuedName)!.SetValue(historyOwner, (ulong)history.Count);
            object childOwner = Assert.Single(((IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(native)!).Values.Cast<object>());
            IList admittedHistory = (IList)childOwner.GetType().GetProperty("OriginalVirtualIo")!.GetValue(childOwner)!;
            int admittedCount = admittedHistory.Count;
            ExternalOriginalVirtualIoRequest nextRequest = new ExternalOriginalVirtualIoRequest(request.Child, request.Device,
                new(new(Guid.NewGuid()), new(987654)));
            Assert.Null(native.BindOriginalVirtualIo(nextRequest));
            Assert.Null(native.BindOriginalVirtualIo(nextRequest));
            Assert.Equal(admittedCount, admittedHistory.Count);
            Assert.False(native.TransferOriginalVirtualIo(request, 0, read, false));
            Assert.Null(native.ResolveOriginalVirtualIo(request));
            Assert.False(kernel.CloseVirtualIo(bound.Value!).IsSuccess);
            Assert.False(kernel.CloseVirtualIo(bound.Value!).IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Same(backing, nativeDevice.GetType().GetField("Backing")!.GetValue(nativeDevice));
            Assert.Equal(new byte[] { 17, 19 }, backing.Take(2).ToArray());
            Assert.Equal(1, model.ActiveDeviceLeaseCount); Assert.Equal(1, foreign.ActiveDeviceLeaseCount);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(1024UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(1, spy.Calls["BindOriginalParentDevice"]); Assert.Equal(1, spy.Calls["BindOriginalVirtualIo"]);
            Assert.InRange(spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"), 0, 1);
            Assert.Equal(0, spy.Calls.GetValueOrDefault("CloseOriginalParentDevice"));
            return;
        }
        if (fault == "history-loss")
        {
            IDictionary children = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(native)!;
            object child = Assert.Single(children.Values.Cast<object>());
            ((IList)child.GetType().GetProperty("OriginalVirtualIo")!.GetValue(child)!).Clear();
            Assert.False(kernel.CloseVirtualIo(bound.Value!).IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess); Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1, model.ActiveDeviceLeaseCount); Assert.Equal(1, foreign.ActiveDeviceLeaseCount);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(1024UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            return;
        }
        if (fault == "close-reentry") spy.After = name =>
        {
            Assert.False(Monitor.IsEntered(typeof(HybridCpuExecutableChildAdapter).GetField("sync", Private)!.GetValue(adapter)!));
            if (name == "CloseOriginalVirtualIo") Assert.False(kernel.CloseVirtualIo(bound.Value!).IsSuccess);
        };
        object? originalCharge = null; object? local = null; PropertyInfo? chargeRoute = null;
        if (fault is "charge-route-loss" or "charge-history-loss")
        {
            IDictionary localOwners = (IDictionary)typeof(RuntimeKernel).GetField("_virtualIo", Private)!.GetValue(kernel)!;
            local = Assert.Single(localOwners.Values.Cast<object>());
            chargeRoute = local.GetType().GetProperty("BackingChargeOwner", Private)!;
            originalCharge = chargeRoute.GetValue(local)!;
            BudgetReservationHandle reservation = (BudgetReservationHandle)local.GetType().GetProperty("BackingReservation", Private)!.GetValue(local)!;
            Assert.False(kernel.ReleaseBudget(owner, reservation).IsSuccess);
            kernel.BeforeVirtualIoClosureLocalCommit = () =>
            {
                kernel.BeforeVirtualIoClosureLocalCommit = null;
                if (fault == "charge-route-loss") chargeRoute.SetValue(local, null);
                else
                {
                    Array accounts = (Array)originalCharge.GetType().GetProperty("OriginalAccounts")!.GetValue(originalCharge)!;
                    object account = accounts.GetValue(0)!;
                    ((IList)account.GetType().GetProperty("Reservations")!.GetValue(account)!).Remove(originalCharge);
                }
            };
        }
        PlatformOriginalVirtualIoCloseRequest? originalPlatformClose = null;
        NeutralOriginalVirtualIoCloseRequest? originalNeutralClose = null;
        if (fault.EndsWith("query-recover", StringComparison.Ordinal)) spy.After = name =>
        {
            Assert.False(Monitor.IsEntered(typeof(HybridCpuExecutableChildAdapter).GetField("sync", Private)!.GetValue(adapter)!));
            Assert.False(Monitor.IsEntered(typeof(HybridCpuPlatformAuthorityProvider).GetField("_childCreateCommitGate", Private)!.GetValue(provider)!));
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", Private)!.GetValue(kernel.PlatformAuthority)!));
            if (name.StartsWith("ResolveOriginal", StringComparison.Ordinal)) Assert.False(kernel.CloseVirtualIo(bound.Value!).IsSuccess);
        };
        KernelResult closed = kernel.CloseVirtualIo(bound.Value!);
        if (fault.EndsWith("query-recover", StringComparison.Ordinal))
        {
            Assert.False(closed.IsSuccess);
            Assert.Equal(1024UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.False(native.TransferOriginalVirtualIo(request, 0, read, false));
            IDictionary ioOwners = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderVirtualIo", Private)!.GetValue(provider)!;
            object ioOwner = Assert.Single(ioOwners.Values.Cast<object>());
            PlatformOriginalVirtualIoCloseRequest platformRequest = (PlatformOriginalVirtualIoCloseRequest)ioOwner.GetType().GetProperty("CloseAdmission")!.GetValue(ioOwner)!;
            NeutralOriginalVirtualIoCloseRequest neutralRequest = (NeutralOriginalVirtualIoCloseRequest)ioOwner.GetType().GetProperty("NativeCloseAdmission")!.GetValue(ioOwner)!;
            originalPlatformClose = platformRequest; originalNeutralClose = neutralRequest;
            spy.Fault = "exact";
            Assert.False(provider.ContinueOriginalVirtualIoClose(platformRequest with { }).IsSuccess);
            Assert.False(provider.ContinueOriginalVirtualIoClose(platformRequest with { InvocationId = Guid.NewGuid() }).IsSuccess);
            Assert.False(adapter.ContinueOriginalVirtualIoClose(neutralRequest with { }).IsSuccess);
            Assert.False(adapter.ContinueOriginalVirtualIoClose(neutralRequest with { Admission = neutralRequest.Admission with { } }).IsSuccess);
            Assert.Equal(1, spy.Calls["CloseOriginalVirtualIo"]);
            closed = kernel.CloseVirtualIo(bound.Value!); // Actual retained composite continuation, no repeated native phase.
        }
        if (fault is "io-close-copy" or "device-close-copy" or "close-query-throw" or "close-query-aba" or "charge-history-loss" or "charge-route-loss")
        {
            Assert.False(closed.IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1024UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(1, spy.Calls["CloseOriginalVirtualIo"]);
            if (fault == "charge-route-loss")
            {
                chargeRoute!.SetValue(local, originalCharge);
                closed = kernel.CloseVirtualIo(bound.Value!); // Same native/provider/bridge facts; quantitative local consumption only.
            }
            else
            {
                Assert.False(kernel.CloseVirtualIo(bound.Value!).IsSuccess); Assert.Equal(1, spy.Calls["CloseOriginalVirtualIo"]);
                Assert.Equal(1, foreign.ActiveDeviceLeaseCount); return;
            }
        }
        Assert.True(closed.IsSuccess, closed.Message);
        if(adapterRead is not null) Assert.Throws<InvalidOperationException>(() => adapterRead.CopyReadData());
        if(providerRead is not null) Assert.Throws<InvalidOperationException>(() => providerRead.CopyReadData());
        if (originalPlatformClose is not null) spy.After = null; // The pending-continuation reentry assertion ends at exact consumption.
        Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.False(native.TransferOriginalVirtualIo(request, 0, read, false));
        Assert.True(kernel.CloseVirtualIo(bound.Value!).IsSuccess);
        if(fault.StartsWith("child-io-history-",StringComparison.Ordinal))
        {
            object ioHistoryChild =Assert.Single(((IDictionary)adapter.GetType().GetField("originalChildren",Private)!.GetValue(adapter)!).Values.Cast<object>());
            IList ioHistory =(IList)ioHistoryChild.GetType().GetProperty("OriginalIo")!.GetValue(ioHistoryChild)!;
            object ioHistoryOriginal =Assert.Single(ioHistory.Cast<object>());
            IDictionary ioHistoryRoutes =(IDictionary)adapter.GetType().GetField("originalIo",Private)!.GetValue(adapter)!;
            object ioHistoryRouteKey =Assert.Single(ioHistoryRoutes.Keys.Cast<object>());
            object? ioHistoryClosure =ioHistoryOriginal.GetType().GetField("Closure")!.GetValue(ioHistoryOriginal);
            NeutralChildDomainLease ioHistoryLease =(NeutralChildDomainLease)ioHistoryChild.GetType().GetProperty("Neutral")!.GetValue(ioHistoryChild)!;
            void DamageIoHistory()
            {
                if(fault.Contains("route",StringComparison.Ordinal))ioHistoryRoutes.Remove(ioHistoryRouteKey);
                else if(fault.Contains("closure",StringComparison.Ordinal))ioHistoryOriginal.GetType().GetField("Closure")!.SetValue(ioHistoryOriginal,null);
                else if(fault.EndsWith("loss",StringComparison.Ordinal))ioHistory.Clear();
                else if(fault.EndsWith("copy",StringComparison.Ordinal))ioHistory[0]=typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(ioHistoryOriginal,null);
                else ioHistoryOriginal.GetType().GetField("Consumed")!.SetValue(ioHistoryOriginal,false);
            }
            void RestoreIoHistory()
            {
                ioHistory.Clear();ioHistory.Add(ioHistoryOriginal);ioHistoryOriginal.GetType().GetField("Consumed")!.SetValue(ioHistoryOriginal,true);
                ioHistoryOriginal.GetType().GetField("Closure")!.SetValue(ioHistoryOriginal,ioHistoryClosure);
                ioHistoryRoutes[ioHistoryRouteKey]=ioHistoryOriginal;
            }
            DamageIoHistory();Assert.False(adapter.CloseChildDomain(ioHistoryLease).IsSuccess);
            Assert.Equal(0,spy.Calls.GetValueOrDefault("CloseOriginalChild"));Assert.Equal(1,foreign.ActiveDeviceLeaseCount);
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            RestoreIoHistory();Assert.True(adapter.CloseChildDomain(ioHistoryLease).IsSuccess);Assert.Equal(1,spy.Calls["CloseOriginalChild"]);
            DamageIoHistory();Assert.False(adapter.ResolveOriginalChildClose(ioHistoryLease).IsSuccess);Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1,spy.Calls["CloseOriginalChild"]);Assert.Equal(1,foreign.ActiveDeviceLeaseCount);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
            RestoreIoHistory();Assert.True(adapter.ResolveOriginalChildClose(ioHistoryLease).IsSuccess);
            object recoveredProviderChild =Assert.Single(((IDictionary)provider.GetType().GetField("_originalProviderChildren",Private)!.GetValue(provider)!).Values.Cast<object>());
            PlatformProviderChildDomainLease recoveredProviderLease =(PlatformProviderChildDomainLease)recoveredProviderChild.GetType().GetProperty("Lease")!.GetValue(recoveredProviderChild)!;
            PlatformAuthorityResult recoveredDrain =provider.TransitionChildDomain(recoveredProviderLease,PlatformChildDomainTransition.BeginDrain);
            object? retainedDrain =recoveredProviderChild.GetType().GetProperty("FailedTransition")!.GetValue(recoveredProviderChild);
            Assert.True(recoveredDrain.IsSuccess,$"{recoveredDrain.Message}; retained={retainedDrain?.GetType().GetProperty("Observation")?.GetValue(retainedDrain)}");
        }
        KernelResult terminated = kernel.TerminateProcess(owner); Assert.True(terminated.IsSuccess, terminated.Message);
        Assert.Equal(0, model.ActiveDeviceLeaseCount); Assert.Equal(1, foreign.ActiveDeviceLeaseCount);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        if (originalPlatformClose is not null)
        {
            Assert.True(provider.ContinueOriginalVirtualIoClose(originalPlatformClose).IsSuccess);
            Assert.True(adapter.ContinueOriginalVirtualIoClose(originalNeutralClose!).IsSuccess);
            Assert.True(provider.RevokeVirtualIo(originalPlatformClose.Lease).IsSuccess);
            Assert.True(adapter.CloseVirtualIo(originalNeutralClose!.Lease).IsSuccess);
            Assert.False(provider.ContinueOriginalVirtualIoClose(originalPlatformClose with { }).IsSuccess);
            Assert.False(adapter.ContinueOriginalVirtualIoClose(originalNeutralClose with { }).IsSuccess);
        }
        Assert.Equal(1, spy.Calls["CloseOriginalVirtualIo"]); Assert.Equal(1, spy.Calls["CloseOriginalParentDevice"]);
    }
    public interface IOriginalBackingRuntime : ExactArtifactReleaseConsumerTests.IReleaseRuntime, IHybridCpuOriginalVirtualIoRuntimeV1, IHybridCpuOriginalVirtualIoContainmentRuntimeV1, IHybridCpuOriginalParentDeviceNoEffectRuntimeV1, IHybridCpuOriginalVirtualIoTransferRuntimeV1 { }
    public class BackingSpy : DispatchProxy
    {
        public HybridCpuExternalRuntime Inner = null!;
        public string Fault = "exact";
        public readonly Dictionary<string, int> Calls = [];
        public Action<string>? After;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            string name = targetMethod!.Name;
            if(Fault=="kernel-read-query-loss" && name=="ResolveOriginalVirtualIoTransfer")throw new IOException("Read snapshot query unavailable");
            Calls[name] = Calls.GetValueOrDefault(name) + 1;
            if (Fault == "refused-query-throw" && name == "ResolveOriginalParentDeviceNoEffect") throw new IOException("No-effect query unavailable.");
            if (Fault == "device-bind-null" && name == "ResolveOriginalParentDevice") return null;
            if ((Fault is "io-bind-query-throw" or "bind-containment-lost" or "bind-containment-query-throw" or "bind-containment-copy" or "bind-charge-route-loss" or "bind-bridge-history-loss" or "bind-containment-aba") && name == "ResolveOriginalVirtualIo" ||
                Fault == "bind-containment-query-throw" && name == "ResolveOriginalVirtualIoBackingContainment") throw new IOException("Original query reply lost.");
            if ((Fault is "close-query-throw" or "close-query-recover" or "io-close-lost-query-recover") && name == "ResolveOriginalVirtualIoClosure" ||
                (Fault is "device-query-recover" or "device-close-lost-query-recover") && name == "ResolveOriginalParentDeviceClosure")
                throw new IOException("Original closure query unavailable.");
            object? result;
            object? busyOwner = null;
            if (Fault.StartsWith("refused", StringComparison.Ordinal) && name == "BindOriginalParentDevice")
            {
                ExternalOriginalParentDeviceRequest admission = (ExternalOriginalParentDeviceRequest)args![0]!;
                busyOwner = ((IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(Inner)!)[admission.ParentAdmission.Lease.Handle]!;
                busyOwner.GetType().GetProperty("InFlight")!.SetValue(busyOwner, admission.Operation);
            }
            try { result = targetMethod.Invoke(Inner, args); }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            { global::System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
            finally { if (busyOwner is not null) busyOwner.GetType().GetProperty("InFlight")!.SetValue(busyOwner, null); }
            After?.Invoke(name);
            if((Fault == "transfer-reply-lost" || Fault=="kernel-read-query-loss") && name == "TransferOriginalVirtualIo") throw new IOException("Transfer reply lost after bytes");
            if (Fault == "refused-lost" && name == "BindOriginalParentDevice") throw new IOException("Original refusal reply lost.");
            if (Fault == "refused-copy" && name == "ResolveOriginalParentDeviceNoEffect") return ((ExternalOriginalParentDeviceNoEffect)result!) with { };
            if (Fault == "device-bind-null" && name == "BindOriginalParentDevice") return null;
            if (Fault == "device-bind-copy" && name == "BindOriginalParentDevice") return ((ExternalOriginalParentDeviceReceipt)result!) with { };
            if (Fault == "bind-containment-lost" && name == "ContainOriginalVirtualIoBacking") throw new IOException("Actual containment reply lost.");
            if (Fault == "bind-containment-copy" && name == "ContainOriginalVirtualIoBacking") return ((ExternalOriginalVirtualIoContainmentClosure)result!) with { };
            if (Fault == "device-bind-lost" && name == "BindOriginalParentDevice" || Fault == "io-bind-lost" && name == "BindOriginalVirtualIo" ||
                (Fault is "io-close-lost" or "io-close-lost-query-recover") && name == "CloseOriginalVirtualIo" ||
                (Fault is "device-close-lost" or "device-close-lost-query-recover") && name == "CloseOriginalParentDevice")
                throw new IOException("Original producer reply lost after actual effect.");
            if (Fault == "io-bind-copy" && name == "BindOriginalVirtualIo") return ((ExternalChildVirtualIoBindReceipt)result!) with { };
            if (Fault == "io-close-copy" && name == "CloseOriginalVirtualIo") return ((ExternalOriginalVirtualIoClosure)result!) with { };
            if (Fault == "device-close-copy" && name == "CloseOriginalParentDevice") return ((ExternalOriginalParentDeviceClosure)result!) with { };
            return result;
        }
    }
}
