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
namespace SingPlus.Tests.Platform;
public sealed class ExactVirtualIoBackingConsumerTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
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
    [InlineData("refused")] [InlineData("refused-lost")]
    [InlineData("refused-query-throw")] [InlineData("refused-copy")]
    [InlineData("refused-op-exhausted")]
    public void ActualNativeLoanAndExternalBackingControlProcessRegionAndChargeReclaim(string fault)
    {
        var model = new NeutralDomainRuntimeFacade();
        var native = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IOriginalBackingRuntime, BackingSpy>();
        var spy = (BackingSpy)proxy; spy.Inner = native; spy.Fault = fault;
        var constructor = typeof(HybridCpuExecutableChildAdapter).GetConstructor(Private, null,
            [typeof(IHybridCpuExternalRuntime), typeof(IHybridCpuChildDomainRuntimeV3), typeof(ISemanticTraceSinkV1)], null)!;
        var rootProxy=DispatchProxy.Create<ExactArtifactReleaseConsumerTests.IOriginalRootRuntime,ExactArtifactReleaseConsumerTests.RootSpy>();
        var rootSpy=(ExactArtifactReleaseConsumerTests.RootSpy)rootProxy;rootSpy.Inner=native;
        var adapter = (HybridCpuExecutableChildAdapter)constructor.Invoke([fault=="kernel-read-native-generation"?rootProxy:native, proxy, null]);
        spy.After = name =>
        {
            Assert.False(Monitor.IsEntered(typeof(HybridCpuExecutableChildAdapter).GetField("sync", Private)!.GetValue(adapter)!));
            if (fault == "source-aba" && name == "BindOriginalVirtualIo" || fault == "close-query-aba" && name == "ResolveOriginalVirtualIoClosure")
            {
                var children = (IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("children", Private)!.GetValue(adapter)!;
                var child = Assert.Single(children.Values.Cast<object>()); var state = child.GetType().GetProperty("State")!;
                state.SetValue(child, state.GetValue(child));
            }
            if (fault.StartsWith("bind-duplicate-", StringComparison.Ordinal) && name == "BindOriginalVirtualIo")
            {
                var nativeRoot = Assert.Single(((IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(native)!).Values.Cast<object>());
                var devicesHistory = (IList)nativeRoot.GetType().GetProperty("OriginalParentDevices")!.GetValue(nativeRoot)!;
                var backingOwner = Assert.Single(devicesHistory.Cast<object>());
                var backing = (byte[])backingOwner.GetType().GetField("Backing")!.GetValue(backingOwner)!;
                backing[0] = 17;
                var historyOwner = fault == "bind-duplicate-device" ? nativeRoot : backingOwner;
                var historyName = fault == "bind-duplicate-device" ? "OriginalParentDevices" : "Users";
                var issuedName = fault == "bind-duplicate-device" ? "OriginalParentDevicesIssued" : "UsersIssued";
                var history = (IList)historyOwner.GetType().GetProperty(historyName)!.GetValue(historyOwner)!;
                history.Add(history[0]);
                if (historyOwner.GetType().GetField(issuedName) is { } issued) issued.SetValue(historyOwner, (ulong)history.Count);
                else historyOwner.GetType().GetProperty(issuedName)!.SetValue(historyOwner, (ulong)history.Count);
            }
            if (fault == "bind-history-loss" && name == "BindOriginalVirtualIo")
            {
                var children = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(native)!;
                var child = Assert.Single(children.Values.Cast<object>());
                ((IList)child.GetType().GetProperty("OriginalVirtualIo")!.GetValue(child)!).Clear();
            }
        };
        var provider = new HybridCpuPlatformAuthorityProvider(model, adapter);
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 783, 1127);
        var admin = TestFixtures.Create(kernel, 99, 999).Handle;
        var budgetCap = kernel.MintCapability(new(999), admin, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "original-io-backing", [new(ServiceBudgetDimension.OwnedMemoryBytes, fault is "exact" or "session-binding" or "session-read-unknown" or "session-read-owner-copy" or "session-read-partial" or "session-read-auto-teardown" || fault.StartsWith("kernel-read-",StringComparison.Ordinal) && fault!="kernel-read-capacity" ? 1028UL : 1024UL)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var created = kernel.CreateVirtualDomain(owner, create, new(1, 4096)); Assert.True(created.IsSuccess, created.Message);
        var domain = created.Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        var roots = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", Private)!.GetValue(kernel.PlatformAuthority)!;
        var rootOwner = Assert.Single(roots.Values.Cast<object>());
        var root = (PlatformDomainBinding)rootOwner.GetType().GetProperty("Binding")!.GetValue(rootOwner)!;
        var source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Device, "device:original-io", CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!.CapabilityId;
        var deviceAdmission = kernel.BindPlatformDevice(owner, root, source, PlatformDeviceRights.Read | PlatformDeviceRights.Write); Assert.True(deviceAdmission.IsSuccess, deviceAdmission.Message);
        var device = deviceAdmission.Value!;
        KernelResult<VirtualIoBinding> bound;
        if(fault is "session-binding" or "session-read-unknown" or "session-read-owner-copy" or "session-read-partial" or "session-read-auto-teardown")
        {
            var service=TestFixtures.Create(kernel,106,1006).Handle;
            var protocol=IVirtualizationServiceProtocol.CreateDefinition();
            var descriptor=kernel.RegisterService(service,"actual-session-bind",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,IVirtualizationServiceResponseProtocol.Definition,[]).Value!;
            var session=kernel.OpenSession(owner,descriptor,[]).Value;
            var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
            var client=IVirtualizationServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
            var invalidAccess=client.BindIoAsync(new(domain.Domain,device.LeaseId.Value,device.Generation.Value,(VirtualIoAccess)255,512)).AsTask();
            Assert.False(host.ProcessNext().IsSuccess);Assert.ThrowsAny<Exception>(()=>invalidAccess.GetAwaiter().GetResult());
            Assert.Equal(0,spy.Calls.GetValueOrDefault("BindOriginalParentDevice"));
            var stranger=TestFixtures.Create(kernel,107,1007).Handle;
            var foreignSession=kernel.OpenSession(stranger,descriptor,[]).Value;
            var foreignHost=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,foreignSession).Value!;
            var foreignClient=IVirtualizationServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel,stranger,foreignSession));
            var foreignBind=foreignClient.BindIoAsync(new(domain.Domain,device.LeaseId.Value,device.Generation.Value,VirtualIoAccess.Read,512)).AsTask();
            Assert.False(foreignHost.ProcessNext().IsSuccess);Assert.ThrowsAny<Exception>(()=>foreignBind.GetAwaiter().GetResult());
            Assert.Equal(0,spy.Calls.GetValueOrDefault("BindOriginalParentDevice"));
            var opened=client.BindIoAsync(new(domain.Domain,device.LeaseId.Value,device.Generation.Value,VirtualIoAccess.Read | VirtualIoAccess.Write,512)).AsTask();
            var issued=host.ProcessNext();Assert.True(issued.IsSuccess,issued.Message);
            var handle=opened.GetAwaiter().GetResult();bound=KernelResult<VirtualIoBinding>.Ok(new(handle.IoId,handle.Generation));
            var sent=client.TransferIoAsync(new(handle.IoId,handle.Generation,0,true,2,new BoundedBytes(new byte[]{71,73}))).AsTask();
            Assert.True(host.ProcessNext().IsSuccess);Assert.True(sent.GetAwaiter().GetResult().Completed);
            if(fault is "session-read-unknown" or "session-read-owner-copy" or "session-read-partial" or "session-read-auto-teardown")
            {
                spy.Fault="kernel-read-query-loss";
                var lost=client.TransferIoAsync(new(handle.IoId,handle.Generation,0,false,2,default)).AsTask();
                Assert.False(host.ProcessNext().IsSuccess);Assert.ThrowsAny<Exception>(()=>lost.GetAwaiter().GetResult());
                Assert.Equal(2,spy.Calls["TransferOriginalVirtualIo"]);
                Assert.Equal(1026UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
                if(fault=="session-read-auto-teardown")
                {
                    spy.Fault="exact";
                    var autoCallerTeardown=kernel.TerminateProcess(owner);Assert.True(autoCallerTeardown.IsSuccess,autoCallerTeardown.Message);
                    Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                    Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
                    Assert.Equal(2,spy.Calls["TransferOriginalVirtualIo"]);Assert.Equal(1,spy.Calls["CloseOriginalVirtualIo"]);
                    Assert.True(kernel.Processes.Resolve(stranger).IsSuccess);Assert.True(kernel.Processes.Resolve(service).IsSuccess);
                    Assert.True(kernel.CloseSession(stranger,foreignSession).IsSuccess);
                    return;
                }
                Assert.True(kernel.CloseSession(stranger,foreignSession).IsSuccess);
                spy.Fault="exact";
                var sessionAlreadyClosed=false;
                if(fault=="session-read-partial")
                {
                    var ioOwner=Assert.Single(((IDictionary)kernel.GetType().GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>());
                    var history=(IList)ioOwner.GetType().GetProperty("Transfers",Private)!.GetValue(ioOwner)!;
                    var readFact=Assert.Single(history.Cast<object>().Where(f=>!(bool)f.GetType().GetField("Request",Private)!.GetValue(f)!.GetType().GetProperty("Write",Private)!.GetValue(f.GetType().GetField("Request",Private)!.GetValue(f))!));
                    var chargeLink=readFact.GetType().GetField("ChargeOwner",Private)!;var partialReadCharge=chargeLink.GetValue(readFact)!;
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
                    var invocations=kernel.GetType().GetField("_endpointSessionInvocationRegistry",Private)!.GetValue(kernel)!;
                    var records=(IDictionary)invocations.GetType().GetField("_ownedRecords",Private)!.GetValue(invocations)!;
                    var invocationOwner=Assert.Single(records.Values.Cast<object>().Where(r=>(bool)r.GetType().GetProperty("AcceptedResponseCancelled")!.GetValue(r)!));
                    var ownerLink=invocationOwner.GetType().GetProperty("OriginalVirtualIoTransfer")!;
                    var originalTransfer=ownerLink.GetValue(invocationOwner)!;
                    var exactInvocation=(EndpointSessionInvocationHandle)invocationOwner.GetType().GetProperty("Handle")!.GetValue(invocationOwner)!;
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
                    var unknownIoClose=client.CloseIoAsync(new(handle.IoId,handle.Generation)).AsTask();
                    Assert.True(host.ProcessNext().IsSuccess);unknownIoClose.GetAwaiter().GetResult();
                }
                Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                if(!sessionAlreadyClosed)Assert.True(kernel.CloseSession(owner,session).IsSuccess);
                var unknownOwnerTermination=kernel.TerminateProcess(owner);Assert.True(unknownOwnerTermination.IsSuccess,unknownOwnerTermination.Message);
                Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
                Assert.Equal(2,spy.Calls["TransferOriginalVirtualIo"]);Assert.Equal(1,spy.Calls["CloseOriginalVirtualIo"]);
                return;
            }
            var received=client.TransferIoAsync(new(handle.IoId,handle.Generation,0,false,2,default)).AsTask();
            Assert.True(host.ProcessNext().IsSuccess);Assert.Equal(new byte[]{71,73},received.GetAwaiter().GetResult().Data.ToArray());
            Assert.Equal(1026UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            var foreignRead=foreignClient.TransferIoAsync(new(handle.IoId,handle.Generation,0,false,2,default)).AsTask();
            Assert.False(foreignHost.ProcessNext().IsSuccess);Assert.ThrowsAny<Exception>(()=>foreignRead.GetAwaiter().GetResult());
            var foreignClose=foreignClient.CloseIoAsync(new(handle.IoId,handle.Generation)).AsTask();
            Assert.False(foreignHost.ProcessNext().IsSuccess);Assert.ThrowsAny<Exception>(()=>foreignClose.GetAwaiter().GetResult());
            var staleRead=client.TransferIoAsync(new(handle.IoId,handle.Generation+1,0,false,2,default)).AsTask();
            Assert.False(host.ProcessNext().IsSuccess);Assert.ThrowsAny<Exception>(()=>staleRead.GetAwaiter().GetResult());
            Assert.Equal(2,spy.Calls["TransferOriginalVirtualIo"]);Assert.Equal(0,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
            Assert.Equal(1026UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.True(kernel.CloseSession(stranger,foreignSession).IsSuccess);
            var sessionClose=client.CloseIoAsync(new(handle.IoId,handle.Generation)).AsTask();
            Assert.True(host.ProcessNext().IsSuccess);sessionClose.GetAwaiter().GetResult();
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            var ownSessionClose=kernel.CloseSession(owner,session);Assert.True(ownSessionClose.IsSuccess,ownSessionClose.Message);
            var ownTermination=kernel.TerminateProcess(owner);Assert.True(ownTermination.IsSuccess,ownTermination.Message);
            Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
            Assert.Equal(2,spy.Calls["TransferOriginalVirtualIo"]);Assert.Equal(1,spy.Calls["CloseOriginalVirtualIo"]);
            return;
        }
        bound = kernel.BindVirtualIo(owner, domain.Domain, device, new(PlatformDeviceRights.Read | PlatformDeviceRights.Write, 512));
        Assert.Equal(1024UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        if (fault.StartsWith("bind-duplicate-", StringComparison.Ordinal) || fault.StartsWith("refused", StringComparison.Ordinal) || fault is "io-bind-copy" or "device-bind-null" or "device-bind-copy" or "io-bind-query-throw" or "bind-containment-lost" or "bind-containment-query-throw" or "bind-containment-copy" or "bind-history-loss" or "bind-charge-route-loss" or "bind-bridge-history-loss" or "bind-containment-aba")
        {
            Assert.False(bound.IsSuccess);
            Assert.False(kernel.BindVirtualIo(owner, domain.Domain, device, new(PlatformDeviceRights.Read, 512)).IsSuccess);
            var unknownLocal = Assert.Single(((IDictionary)typeof(RuntimeKernel).GetField("_virtualIo", Private)!.GetValue(kernel)!).Values.Cast<object>());
            var unknownBinding = (VirtualIoBinding)unknownLocal.GetType().GetProperty("Binding", Private)!.GetValue(unknownLocal)!;
            var unknownRequest = (PlatformOriginalVirtualIoBindRequest)unknownLocal.GetType().GetProperty("OriginalBindRequest", Private)!.GetValue(unknownLocal)!;
            Assert.False(provider.CleanupOriginalVirtualIoBind(unknownRequest with { }).IsSuccess);
            Assert.Null(provider.ResolveOriginalVirtualIoBindCleanup(unknownRequest with { }));
            var owners = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderVirtualIo", Private)!.GetValue(provider)!;
            var original = Assert.Single(owners.Values.Cast<object>());
            var neutral = (NeutralOriginalVirtualIoRequest)original.GetType().GetProperty("NativeOriginalRequest")!.GetValue(original)!;
            Assert.False(adapter.CleanupOriginalVirtualIoBind(neutral with { }).IsSuccess);
            var unknownDevices = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_deviceLeases", Private)!.GetValue(provider)!;
            var unknownDevice = (NeutralDeviceLease)Assert.Single(unknownDevices.Values.Cast<object>()).GetType().GetProperty("HybridCpuLease")!.GetValue(Assert.Single(unknownDevices.Values.Cast<object>()))!;
            var unknownForeign = new NeutralDomainRuntimeFacade(); var unknownForeignRoot = unknownForeign.Bind(NeutralDomainProfile.OrdinaryService).Lease;
            var unknownForeignDevice = unknownForeign.BindDevice(unknownForeignRoot, unknownDevice.Device, unknownDevice.Rights).Lease;
            Assert.Equal(unknownDevice, unknownForeignDevice);
            Assert.NotEqual(NeutralDeviceCloseDecision.Closed, model.CloseDevice(unknownDevice).Decision);
            if (fault == "refused-op-exhausted") typeof(HybridCpuExecutableChildAdapter).GetField("nextOperation", Private)!.SetValue(adapter, ulong.MaxValue);
            if (fault == "bind-bridge-history-loss")
            {
                var bridgeDevice = Assert.Single(((IDictionary)typeof(PlatformAuthorityBridge).GetField("_originalDeviceOwners", Private)!.GetValue(kernel.PlatformAuthority)!).Values.Cast<object>());
                ((IList)bridgeDevice.GetType().GetProperty("VirtualIoBindInvocations")!.GetValue(bridgeDevice)!).Clear();
            }
            var quantumRoute = unknownLocal.GetType().GetProperty("BackingChargeOwner", Private)!;
            var quantumOwner = quantumRoute.GetValue(unknownLocal);
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
                    var child = Assert.Single(((IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("children", Private)!.GetValue(adapter)!).Values.Cast<object>());
                    var state = child.GetType().GetProperty("State")!; state.SetValue(child, state.GetValue(child));
                }
            };
            if (fault == "device-bind-null")
            {
                var terminatedUnknown = kernel.TerminateProcess(owner); Assert.True(terminatedUnknown.IsSuccess, terminatedUnknown.Message);
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
            var unknownClosed = kernel.CloseVirtualIo(unknownBinding);
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
                        var nativeRoot = Assert.Single(((IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(native)!).Values.Cast<object>());
                        var backingOwner = ((IList)nativeRoot.GetType().GetProperty("OriginalParentDevices")!.GetValue(nativeRoot)!)[0]!;
                        var backing = (byte[])backingOwner.GetType().GetField("Backing")!.GetValue(backingOwner)!;
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
        var facts = (IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("originalIo", Private)!.GetValue(adapter)!;
        var fact = Assert.Single(facts.Values.Cast<object>());
        var request = (ExternalOriginalVirtualIoRequest)fact.GetType().GetField("NativeRequest")!.GetValue(fact)!;
        Assert.True(native.TransferOriginalVirtualIo(request, 0, [17, 19], true));
        byte[] read = new byte[2]; Assert.True(native.TransferOriginalVirtualIo(request, 0, read, false)); Assert.Equal(new byte[] { 17, 19 }, read);
        NeutralOriginalVirtualIoTransferReceipt? adapterRead = null;
        PlatformOriginalVirtualIoTransferReceipt? providerRead = null;
        if(fault == "exact")
        {
            var lease = (NeutralVirtualIoLease)fact.GetType().GetField("Lease")!.GetValue(fact)!;
            var transfer = new NeutralOriginalVirtualIoTransferRequest(lease,0,true,2,new byte[]{23,29});
            spy.Fault = "transfer-reply-lost";
            var written = adapter.TransferOriginalVirtualIo(transfer); Assert.True(written.IsSuccess,written.Reason);
            Assert.Same(written.Value,adapter.ResolveOriginalVirtualIoTransfer(transfer).Value);
            Assert.Same(written.Value,adapter.TransferOriginalVirtualIo(transfer).Value);
            spy.Fault = "exact";
            var readTransfer = new NeutralOriginalVirtualIoTransferRequest(lease,0,false,2);
            var observed = adapter.TransferOriginalVirtualIo(readTransfer); Assert.True(observed.IsSuccess,observed.Reason);
            adapterRead = observed.Value;
            Assert.Equal(new byte[]{23,29},adapterRead!.CopyReadData());
            Assert.Equal(2,spy.Calls["TransferOriginalVirtualIo"]);
            var providerIo = Assert.Single(((IDictionary)provider.GetType().GetField("_originalProviderVirtualIo",Private)!.GetValue(provider)!).Values.Cast<object>());
            var providerLease = (PlatformProviderVirtualIoLease)providerIo.GetType().GetProperty("Lease")!.GetValue(providerIo)!;
            var providerTransfer = new PlatformOriginalVirtualIoTransferRequest(providerLease,4,true,2,new byte[]{31,37});
            spy.Fault = "transfer-reply-lost";
            var sent = provider.TransferOriginalVirtualIo(providerTransfer); Assert.True(sent.IsSuccess,sent.Message);
            Assert.Same(sent.Value,provider.ResolveOriginalVirtualIoTransfer(providerTransfer).Value);
            Assert.Same(sent.Value,provider.TransferOriginalVirtualIo(providerTransfer).Value);
            spy.Fault = "exact";
            var received = provider.TransferOriginalVirtualIo(new PlatformOriginalVirtualIoTransferRequest(providerLease,4,false,2));
            Assert.True(received.IsSuccess,received.Message); providerRead = received.Value;
            Assert.Equal(new byte[]{31,37},providerRead!.CopyReadData());
            Assert.Equal(4,spy.Calls["TransferOriginalVirtualIo"]);
            var localIo = Assert.Single(((IDictionary)kernel.GetType().GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>());
            var platformIo = (PlatformVirtualIoBinding)localIo.GetType().GetProperty("Platform",Private)!.GetValue(localIo)!;
            var preparedWrite = kernel.PlatformAuthority.PrepareVirtualIoTransfer(platformIo,8,true,2,new byte[]{41,43});
            Assert.True(preparedWrite.IsSuccess,preparedWrite.Message);
            spy.Fault = "transfer-reply-lost";
            var bridgeWrite = kernel.PlatformAuthority.InvokeVirtualIoTransfer(platformIo,preparedWrite.Value!);
            Assert.True(bridgeWrite.IsSuccess,bridgeWrite.Message);
            Assert.Same(bridgeWrite.Value,kernel.PlatformAuthority.InvokeVirtualIoTransfer(platformIo,preparedWrite.Value!).Value);
            spy.Fault = "exact";
            Assert.Equal(5,spy.Calls["TransferOriginalVirtualIo"]);
            var bridgeBytes = new byte[2]; Assert.True(native.TransferOriginalVirtualIo(request,8,bridgeBytes,false));
            Assert.Equal(new byte[]{41,43},bridgeBytes);
            Assert.False(kernel.TransferVirtualIo(owner,new KernelVirtualIoTransferRequest(bound.Value!,0,true,4097,new byte[4097])).IsSuccess);
            Assert.False(kernel.TransferVirtualIo(owner,new KernelVirtualIoTransferRequest(bound.Value!,0,false,2,new byte[2])).IsSuccess);
            var kernelWrite = new KernelVirtualIoTransferRequest(bound.Value!,12,true,2,new byte[]{47,53});
            spy.Fault="transfer-reply-lost";
            var kernelSent=kernel.TransferVirtualIo(owner,kernelWrite); Assert.True(kernelSent.IsSuccess,kernelSent.Message);
            Assert.Same(kernelSent.Value,kernel.TransferVirtualIo(owner,kernelWrite).Value);
            spy.Fault="exact";
            var kernelReadRequest=new KernelVirtualIoTransferRequest(bound.Value!,12,false,2);
            var kernelRead=kernel.TransferVirtualIo(owner,kernelReadRequest); Assert.True(kernelRead.IsSuccess,kernelRead.Message);
            Assert.Equal(new byte[]{47,53},kernelRead.Value!.CopyReadData());
            Assert.Same(kernelRead.Value,kernel.TransferVirtualIo(owner,kernelReadRequest).Value);
            Assert.Equal(1026UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(7,spy.Calls["TransferOriginalVirtualIo"]);
            var service=TestFixtures.Create(kernel,105,1005).Handle;
            var protocol=IVirtualizationServiceProtocol.CreateDefinition();
            var contract=new ServiceContractIdentity(protocol.ContractName,"1",protocol.ContractDigest);
            var descriptor=kernel.RegisterService(service,"actual-io-data",contract,protocol,IVirtualizationServiceResponseProtocol.Definition,[]).Value!;
            var session=kernel.OpenSession(owner,descriptor,[]).Value;
            var serviceHost=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
            var client=IVirtualizationServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
            var writeTask=client.TransferIoAsync(new(bound.Value!.Id,bound.Value.Generation,16,true,2,new BoundedBytes(new byte[]{59,61}))).AsTask();
            Assert.True(serviceHost.ProcessNext().IsSuccess);Assert.True(writeTask.GetAwaiter().GetResult().Completed);
            var readTask=client.TransferIoAsync(new(bound.Value.Id,bound.Value.Generation,16,false,2,default)).AsTask();
            Assert.True(serviceHost.ProcessNext().IsSuccess);Assert.Equal(new byte[]{59,61},readTask.GetAwaiter().GetResult().Data.ToArray());
            Assert.Equal(1028UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(9,spy.Calls["TransferOriginalVirtualIo"]);
            Assert.True(kernel.CloseSession(owner,session).IsSuccess);
        }
        var devices = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_deviceLeases", Private)!.GetValue(provider)!;
        var providerDevice = Assert.Single(devices.Values.Cast<object>());
        var originalDevice = (NeutralDeviceLease)providerDevice.GetType().GetProperty("HybridCpuLease")!.GetValue(providerDevice)!;
        var foreign = new NeutralDomainRuntimeFacade(); var foreignRoot = foreign.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var foreignDevice = foreign.BindDevice(foreignRoot, originalDevice.Device, originalDevice.Rights).Lease;
        Assert.Equal(originalDevice, foreignDevice);
        Assert.NotEqual(NeutralDeviceCloseDecision.Closed, model.CloseDevice(originalDevice).Decision); // Existing native owner sees the actual external borrower.
        if(fault.StartsWith("kernel-read-",StringComparison.Ordinal))
        {
            var transfer = new KernelVirtualIoTransferRequest(bound.Value!,0,false,2);
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
                var originalIo=Assert.Single(((IDictionary)kernel.GetType().GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>());
                IList? replacedHistory=null;object? originalTransferFact=null;
                var originalIncarnation=model.MappingClosureIncarnation;
                spy.After=name=>
                {
                    if(name!="TransferOriginalVirtualIo")return;
                    Assert.False(kernel.CloseVirtualIo(bound.Value!).IsSuccess);
                    if(fault=="kernel-read-model-incarnation")typeof(NeutralDomainRuntimeFacade).GetField("<MappingClosureIncarnation>k__BackingField",Private)!.SetValue(model,Guid.NewGuid());
                    else if(fault is "kernel-read-adapter-copy" or "kernel-read-provider-copy")
                    {
                        var historyOwner= fault=="kernel-read-adapter-copy" ? Assert.Single(((IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("originalIo",Private)!.GetValue(adapter)!).Values.Cast<object>()) : Assert.Single(((IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderVirtualIo",Private)!.GetValue(provider)!).Values.Cast<object>());
                        var history=(IList)historyOwner.GetType().GetProperty("Transfers")!.GetValue(historyOwner)!;
                        replacedHistory=history;originalTransferFact=history[0];
                        history[0]=typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(history[0],null);
                    }
                    else if(fault=="kernel-read-native-generation")rootSpy.FeatureGenerationOverride=native.QueryFeatures().Generation+1;
                    else if(fault=="kernel-read-adapter-revision")
                    {
                        var child=Assert.Single(((IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("children",Private)!.GetValue(adapter)!).Values.Cast<object>());
                        var state=child.GetType().GetProperty("State")!;state.SetValue(child,state.GetValue(child));
                    }
                    else if(fault=="kernel-read-source-revision")
                    {
                        var state=originalIo.GetType().GetProperty("State",Private)!;
                        state.SetValue(originalIo,state.GetValue(originalIo));
                    }
                    else
                    {
                        var history=(IList)originalIo.GetType().GetProperty("Transfers",Private)!.GetValue(originalIo)!;
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
                    var transferOwner=Assert.Single(((IList)originalIo.GetType().GetProperty("Transfers",Private)!.GetValue(originalIo)!).Cast<object>());
                    var captured=transferOwner.GetType().GetField("Revision",Private)!.GetValue(transferOwner);
                    originalIo.GetType().GetProperty("SourceRevision",Private)!.SetValue(originalIo,captured);
                    Assert.False(kernel.TransferVirtualIo(owner,transfer).IsSuccess); // Equal projection cannot undo observed source loss.
                }
                Assert.Equal(1,spy.Calls["TransferOriginalVirtualIo"]);
                Assert.Equal(1026UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                var close=kernel.CloseVirtualIo(bound.Value!);
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
            var resolved=kernel.TransferVirtualIo(owner,transfer); Assert.True(resolved.IsSuccess,resolved.Message);
            Assert.Equal(new byte[]{17,19},resolved.Value!.CopyReadData());
            Assert.Equal(1,spy.Calls["TransferOriginalVirtualIo"]);
            var localIo=Assert.Single(((IDictionary)kernel.GetType().GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>());
            var transferFact=Assert.Single(((IList)localIo.GetType().GetProperty("Transfers",Private)!.GetValue(localIo)!).Cast<object>());
            var chargeField=transferFact.GetType().GetField("ChargeOwner",Private)!;
            var snapshotCharge=chargeField.GetValue(transferFact);
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
            var rootInventory = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(native)!;
            var nativeRoot = Assert.Single(rootInventory.Values.Cast<object>());
            var nativeDevices = (IList)nativeRoot.GetType().GetProperty("OriginalParentDevices")!.GetValue(nativeRoot)!;
            var nativeDevice = Assert.Single(nativeDevices.Cast<object>());
            var backing = (byte[])nativeDevice.GetType().GetField("Backing")!.GetValue(nativeDevice)!;
            object historyOwner = nativeDevice;
            string historyName = "Users", issuedName = "UsersIssued";
            if (fault == "duplicate-device") { historyOwner = nativeRoot; historyName = "OriginalParentDevices"; issuedName = "OriginalParentDevicesIssued"; }
            if (fault == "duplicate-child")
            {
                historyOwner = Assert.Single(((IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(native)!).Values.Cast<object>());
                historyName = "OriginalVirtualIo"; issuedName = "OriginalVirtualIoIssued";
            }
            var history = (IList)historyOwner.GetType().GetProperty(historyName)!.GetValue(historyOwner)!;
            history.Add(history[0]);
            var issuedField = historyOwner.GetType().GetField(issuedName);
            if (issuedField is not null) issuedField.SetValue(historyOwner, (ulong)history.Count);
            else historyOwner.GetType().GetProperty(issuedName)!.SetValue(historyOwner, (ulong)history.Count);
            var childOwner = Assert.Single(((IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(native)!).Values.Cast<object>());
            var admittedHistory = (IList)childOwner.GetType().GetProperty("OriginalVirtualIo")!.GetValue(childOwner)!;
            var admittedCount = admittedHistory.Count;
            var nextRequest = new ExternalOriginalVirtualIoRequest(request.Child, request.Device,
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
            var children = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(native)!;
            var child = Assert.Single(children.Values.Cast<object>());
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
            var localOwners = (IDictionary)typeof(RuntimeKernel).GetField("_virtualIo", Private)!.GetValue(kernel)!;
            local = Assert.Single(localOwners.Values.Cast<object>());
            chargeRoute = local.GetType().GetProperty("BackingChargeOwner", Private)!;
            originalCharge = chargeRoute.GetValue(local)!;
            var reservation = (BudgetReservationHandle)local.GetType().GetProperty("BackingReservation", Private)!.GetValue(local)!;
            Assert.False(kernel.ReleaseBudget(owner, reservation).IsSuccess);
            kernel.BeforeVirtualIoClosureLocalCommit = () =>
            {
                kernel.BeforeVirtualIoClosureLocalCommit = null;
                if (fault == "charge-route-loss") chargeRoute.SetValue(local, null);
                else
                {
                    var accounts = (Array)originalCharge.GetType().GetProperty("OriginalAccounts")!.GetValue(originalCharge)!;
                    var account = accounts.GetValue(0)!;
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
        var closed = kernel.CloseVirtualIo(bound.Value!);
        if (fault.EndsWith("query-recover", StringComparison.Ordinal))
        {
            Assert.False(closed.IsSuccess);
            Assert.Equal(1024UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.False(native.TransferOriginalVirtualIo(request, 0, read, false));
            var ioOwners = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderVirtualIo", Private)!.GetValue(provider)!;
            var ioOwner = Assert.Single(ioOwners.Values.Cast<object>());
            var platformRequest = (PlatformOriginalVirtualIoCloseRequest)ioOwner.GetType().GetProperty("CloseAdmission")!.GetValue(ioOwner)!;
            var neutralRequest = (NeutralOriginalVirtualIoCloseRequest)ioOwner.GetType().GetProperty("NativeCloseAdmission")!.GetValue(ioOwner)!;
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
        var terminated = kernel.TerminateProcess(owner); Assert.True(terminated.IsSuccess, terminated.Message);
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
            var name = targetMethod!.Name;
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
                var admission = (ExternalOriginalParentDeviceRequest)args![0]!;
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
