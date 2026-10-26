using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
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
            object? result =base.Invoke(method,args);
            if(LoseBind && method!.Name=="BindOriginalVirtualIo")throw new IOException("Bind reply lost after backing effect");
            if(LoseContainment && method!.Name=="ContainOriginalVirtualIoBacking")throw new IOException("Containment reply lost after effect");
            return result;
        }
    }

    [Theory]
    [InlineData(false,false,false,null)] [InlineData(true,false,false,null)] [InlineData(false,true,false,null)] [InlineData(true,true,false,null)]
    [InlineData(false,false,true,null)] [InlineData(true,false,true,null)] [InlineData(false,true,true,null)] [InlineData(true,true,true,null)]
    [InlineData(false,true,false,"receipt-copy")] [InlineData(false,true,false,"request-loss")]
    [InlineData(false,true,false,"sdk-request-loss")] [InlineData(false,true,false,"sdk-request-copy")]
    [InlineData(false,true,false,"sdk-callback-raw-copy")]
    [InlineData(false,true,false,"sdk-callback-request-copy")] [InlineData(false,true,false,"sdk-callback-throw")]
    [InlineData(false,true,false,"provider-final-sdk-raw-copy")]
    [InlineData(false,true,false,"bridge-final-provider-closure-copy")]
    [InlineData(false,true,false,"bridge-final-provider-closure-loss")]
    [InlineData(false,true,false,"bridge-local-provider-closure-copy")]
    [InlineData(false,true,false,"bridge-local-provider-closure-loss")]
    [InlineData(false,true,false,"bridge-local-throw")]
    [InlineData(false,false,false,"kernel-bind-session-flag")]
    [InlineData(true,false,false,"kernel-bind-session-flag")]
    [InlineData(false,false,false,"kernel-bind-invocation-copy")]
    [InlineData(false,false,false,"kernel-bind-invocation-loss")]
    [InlineData(false,false,false,"invocation-bind-flags")]
    [InlineData(true,false,false,"invocation-bind-flags")]
    [InlineData(false,false,false,"kernel-bind-bound-flag")]
    [InlineData(true,false,false,"kernel-bind-bound-flag")]
    [InlineData(false,false,false,"kernel-bind-route-loss")]
    public async Task OriginalServiceBindClosesInvocationOnlyAfterBackingAndCharge(bool copyOwner,bool partialClose,bool cancellationThrow,string? damage)
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        ExactVirtualIoBackingConsumerTests.IOriginalBackingRuntime runtime =DispatchProxy.Create<ExactVirtualIoBackingConsumerTests.IOriginalBackingRuntime,BindLossSpy>();
        BindLossSpy spy =(BindLossSpy)(object)runtime;
        spy.Inner=new HybridCPU.ExternalRuntime.HybridCpuExternalRuntime();
        HybridCpuExecutableChildAdapter adapter =new HybridCpuExecutableChildAdapter(spy.Inner,runtime);
        NeutralDomainRuntimeFacade native =new NeutralDomainRuntimeFacade();
        NativeTransferRawCustodyConsumerTests.ISourceRuntime source=DispatchProxy.Create<NativeTransferRawCustodyConsumerTests.ISourceRuntime,NativeTransferRawCustodyConsumerTests.SourceSpy>();
        var sourceSpy=(NativeTransferRawCustodyConsumerTests.SourceSpy)(object)source;sourceSpy.Inner=native;
        HybridCpuPlatformAuthorityProvider provider=new(source,adapter);
        var bridgeProxy=DispatchProxy.Create<ExactVirtualIoBackingConsumerTests.IBridgeBindProvider,ExactVirtualIoBackingConsumerTests.BridgeBindSpy>();
        var bridgeSpy=(ExactVirtualIoBackingConsumerTests.BridgeBindSpy)(object)bridgeProxy;bridgeSpy.Inner=provider;
        RuntimeKernel kernel =new RuntimeKernel(damage?.StartsWith("bridge-",StringComparison.Ordinal)==true?bridgeProxy:provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel,2501,25010);
        ProcessHandle service =TestFixtures.Create(kernel,2502,25020).Handle;
        ProcessHandle admin =TestFixtures.Create(kernel,2503,25030).Handle;
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(service,32).Value!;foreign.Span[0]=0x6A;
        CapabilityId ac =kernel.MintCapability(new(25030),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(admin,ac,owner,"IO Bind",[new(ServiceBudgetDimension.OwnedMemoryBytes,512)]).Value!.ProcessBudget;
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        CapabilityId create =kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain =kernel.CreateVirtualDomain(owner,create,new(1,4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess);
        PlatformDomainBinding root =kernel.QueryVirtualDomainPlatformBinding(owner,domain.Domain).Value;
        CapabilityId dc =kernel.MintCapability(process.DomainId,owner,ResourceKind.Device,"device/io-bind",CapabilityRights.Read|CapabilityRights.Write|CapabilityRights.Configure).Value!.CapabilityId;
        PlatformDeviceLease device =kernel.BindPlatformDevice(owner,root,dc,PlatformDeviceRights.Read|PlatformDeviceRights.Write).Value;
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"IO Bind",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(owner,descriptor,[]).Value;
        RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        IVirtualizationService client =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        ValueTask<VirtualIoHandleResponse> pending =client.BindIoAsync(new(domain.Domain,device.LeaseId.Value,device.Generation.Value,VirtualIoAccess.Read|VirtualIoAccess.Write,512));
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
            object custody =host.GetType().GetField("_pendingOperation",flags)!.GetValue(host)!;
            FieldInfo requestField =custody.GetType().GetField("Request",flags)!;
            ChannelEnvelope originalRequest =(ChannelEnvelope)requestField.GetValue(custody)!;
            requestField.SetValue(custody,originalRequest with { });
            Assert.False(host.AdvancePendingResult().IsSuccess); // Equal request cannot authorize response retry.
            requestField.SetValue(custody,originalRequest);
            FieldInfo lifetimeField =custody.GetType().GetField("Lifetime",flags)!;
            object lifetime =lifetimeField.GetValue(custody)!;
            lifetimeField.SetValue(custody,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(lifetime,null));
            Assert.False(host.AdvancePendingResult().IsSuccess);
            lifetimeField.SetValue(custody,lifetime);
            Assert.True(host.AdvancePendingResult().IsSuccess);
            Assert.False(host.HasPendingResult);
        }
        else Assert.False(host.ProcessNext().IsSuccess);
        await Assert.ThrowsAnyAsync<Exception>(async()=>await pending);
        Assert.Equal(512UL,Used());Assert.Equal(1,spy.Calls["BindOriginalVirtualIo"]);
        EndpointSessionInvocationRegistry registry =(EndpointSessionInvocationRegistry)kernel.GetType().GetField("_endpointSessionInvocationRegistry",flags)!.GetValue(kernel)!;
        IDictionary records =(IDictionary)registry.GetType().GetField("_ownedRecords",flags)!.GetValue(registry)!;
        object record =Assert.Single(records.Values.Cast<object>());
        PropertyInfo property =record.GetType().GetProperty("OriginalVirtualIoBind")!;
        object original =property.GetValue(record)!;
        EndpointSessionInvocationHandle invocation =(EndpointSessionInvocationHandle)record.GetType().GetProperty("Handle")!.GetValue(record)!;
        Assert.False(registry.ConsumeVirtualIoAdmissionClosure(invocation,service,original).IsSuccess);
        Assert.False(registry.ConsumeVirtualIoAdmissionClosure(invocation,owner,new object()).IsSuccess);
        if(damage=="invocation-bind-flags")
        {
            record.GetType().GetProperty("VirtualIoBindContained")!.SetValue(record,true);
            record.GetType().GetProperty("VirtualIoBindNeverInvoked")!.SetValue(record,true);
            Assert.True(kernel.CloseSession(owner,session).IsSuccess);
            Assert.Same(record,Assert.Single(records.Values.Cast<object>()));
            Assert.Equal(512UL,Used());Assert.Equal(0x6A,foreign.Span[0]);
            record.GetType().GetProperty("VirtualIoBindContained")!.SetValue(record,false);
            record.GetType().GetProperty("VirtualIoBindNeverInvoked")!.SetValue(record,false);
        }
        spy.LoseBind=false;
        if(damage=="kernel-bind-session-flag")
        {
            object local=Assert.Single(((IList)kernel.GetType().GetField("_virtualIoOwners",flags)!.GetValue(kernel)!).Cast<object>());
            local.GetType().GetProperty("BindSessionClosureConsumed",flags)!.SetValue(local,true);
        }
        if(damage=="kernel-bind-bound-flag")
        {
            object local=Assert.Single(((IList)kernel.GetType().GetField("_virtualIoOwners",flags)!.GetValue(kernel)!).Cast<object>());
            local.GetType().GetProperty("BindSessionBound",flags)!.SetValue(local,false);
        }
        if(damage=="kernel-bind-route-loss")
        {
            object local=Assert.Single(((IList)kernel.GetType().GetField("_virtualIoOwners",flags)!.GetValue(kernel)!).Cast<object>());
            PropertyInfo slot=local.GetType().GetProperty("BindSessionInvocation",flags)!;
            object exact=slot.GetValue(local)!;
            local.GetType().GetProperty("BindSessionBound",flags)!.SetValue(local,false);slot.SetValue(local,null);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(512UL,Used());
            Assert.Same(record,Assert.Single(records.Values.Cast<object>()));Assert.Equal(0x6A,foreign.Span[0]);
            slot.SetValue(local,exact);
        }
        if(damage?.StartsWith("kernel-bind-invocation-",StringComparison.Ordinal)==true)
        {
            object local=Assert.Single(((IList)kernel.GetType().GetField("_virtualIoOwners",flags)!.GetValue(kernel)!).Cast<object>());
            PropertyInfo slot=local.GetType().GetProperty("BindSessionOriginal",flags)!;
            object exact=slot.GetValue(local)!;
            slot.SetValue(local,damage.EndsWith("loss",StringComparison.Ordinal)?null:typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(exact,null));
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0UL,Used());
            Assert.Same(record,Assert.Single(records.Values.Cast<object>()));
            Assert.False((bool)record.GetType().GetProperty("VirtualIoBindContained")!.GetValue(record)!);
            Assert.Equal(1,spy.Calls["ContainOriginalVirtualIoBacking"]);Assert.Equal(0x6A,foreign.Span[0]);
            slot.SetValue(local,exact);
        }
        if(partialClose)
        {
            spy.LoseContainment=true;
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(512UL,Used());Assert.Equal(1,native.ActiveDeviceLeaseCount);
            Assert.False((bool)record.GetType().GetProperty("VirtualIoBindContained")!.GetValue(record)!);
            spy.LoseContainment=false;
            if(damage?.StartsWith("bridge-",StringComparison.Ordinal)==true)
            {
                object providerOwner=Assert.Single(((IDictionary)provider.GetType().GetField("_originalProviderVirtualIo",flags)!.GetValue(provider)!).Values.Cast<object>());
                PropertyInfo slot=providerOwner.GetType().GetProperty("BindCleanup")!;
                object? exact=null;bool observed=false;
                Action damageClosure=()=>
                {
                    if(observed || !(bool)providerOwner.GetType().GetProperty("Consumed")!.GetValue(providerOwner)!)return;
                    observed=true;
                    Assert.False(Monitor.IsEntered(kernel.PlatformAuthority.GetType().GetField("_secureDomainLifecycleGate",flags)!.GetValue(kernel.PlatformAuthority)!));
                    Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                    exact=slot.GetValue(providerOwner)!;
                    if(damage.EndsWith("throw",StringComparison.Ordinal))throw new IOException("Original Bind local consumption interrupted.");
                    slot.SetValue(providerOwner,damage.EndsWith("loss",StringComparison.Ordinal)?null:typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(exact,null));
                };
                if(damage.StartsWith("bridge-final-",StringComparison.Ordinal))bridgeSpy.BeforeSource=damageClosure;
                else kernel.BeforeVirtualIoClosureLocalCommit=damageClosure;
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                Assert.True(observed);Assert.Equal(512UL,Used());
                Assert.True((bool)providerOwner.GetType().GetProperty("Consumed")!.GetValue(providerOwner)!);
                Assert.False((bool)record.GetType().GetProperty("VirtualIoBindContained")!.GetValue(record)!);
                Assert.Equal(1,spy.Calls["ContainOriginalVirtualIoBacking"]);Assert.Equal(0x6A,foreign.Span[0]);
                Assert.Same(exact,providerOwner.GetType().GetProperty("OriginalBindCleanup")!.GetValue(providerOwner));
                bridgeSpy.BeforeSource=null;kernel.BeforeVirtualIoClosureLocalCommit=null;slot.SetValue(providerOwner,exact);
            }
            else if(damage=="provider-final-sdk-raw-copy")
            {
                object providerOwner=Assert.Single(((IDictionary)provider.GetType().GetField("_originalProviderVirtualIo",flags)!.GetValue(provider)!).Values.Cast<object>());
                object sdkOwner=Assert.Single(((IDictionary)adapter.GetType().GetField("originalIo",flags)!.GetValue(adapter)!).Values.Cast<object>());
                FieldInfo slot=sdkOwner.GetType().GetField("Containment")!;
                object? exact=null;bool observed=false;
                sourceSpy.BeforeSource=()=>
                {
                    if(observed || providerOwner.GetType().GetProperty("ValidatedBindCleanup")!.GetValue(providerOwner) is null)return;
                    observed=true;
                    Assert.False(Monitor.IsEntered(provider.GetType().GetField("_childCreateCommitGate",flags)!.GetValue(provider)!));
                    Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                    exact=slot.GetValue(sdkOwner)!;
                    slot.SetValue(sdkOwner,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(exact,null));
                };
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                Assert.True(observed);Assert.Equal(512UL,Used());
                Assert.False((bool)providerOwner.GetType().GetProperty("Consumed")!.GetValue(providerOwner)!);
                Assert.False((bool)record.GetType().GetProperty("VirtualIoBindContained")!.GetValue(record)!);
                Assert.Equal(1,spy.Calls["ContainOriginalVirtualIoBacking"]);Assert.Equal(0x6A,foreign.Span[0]);
                sourceSpy.BeforeSource=null;slot.SetValue(sdkOwner,exact);
            }
            else if(damage?.StartsWith("sdk-",StringComparison.Ordinal)==true)
            {
                object sdkOwner=Assert.Single(((IDictionary)adapter.GetType().GetField("originalIo",flags)!.GetValue(adapter)!).Values.Cast<object>());
                FieldInfo slot=sdkOwner.GetType().GetField("ContainmentRequest")!;
                var exact=(HybridCPU.ExternalRuntime.Contracts.ExternalOriginalVirtualIoContainmentRequest)slot.GetValue(sdkOwner)!;
                object? damaged=damage.EndsWith("loss",StringComparison.Ordinal)?null:exact with {};
                bool callback=damage.StartsWith("sdk-callback-",StringComparison.Ordinal),observed=false;
                var nativeRaw=spy.Inner.ResolveOriginalVirtualIoBackingContainment(exact)!;
                if(callback)spy.After=name=>
                {
                    if(name!="ResolveOriginalVirtualIoBackingContainment" || observed)return;
                    observed=true;
                    Assert.False(Monitor.IsEntered(adapter.GetType().GetField("sync",flags)!.GetValue(adapter)!));
                    Assert.False(kernel.TerminateProcess(owner).IsSuccess); // In-flight reentry cannot consume the owner.
                    if(damage=="sdk-callback-throw")throw new IOException("Original containment query callback lost.");
                    if(damage=="sdk-callback-raw-copy")sdkOwner.GetType().GetField("Containment")!.SetValue(sdkOwner,nativeRaw with {});
                    else slot.SetValue(sdkOwner,damaged);
                };
                else slot.SetValue(sdkOwner,damaged);
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(512UL,Used());
                if(callback)
                {
                    Assert.True(observed);spy.After=null;
                    if(damage!="sdk-callback-throw")Assert.Same(nativeRaw,sdkOwner.GetType().GetField("OriginalContainment")!.GetValue(sdkOwner));
                    sdkOwner.GetType().GetField("Containment")!.SetValue(sdkOwner,sdkOwner.GetType().GetField("OriginalContainment")!.GetValue(sdkOwner));
                }
                else Assert.Same(damaged,slot.GetValue(sdkOwner)); // Missing route cannot reserve a replacement original.
                Assert.Equal(1,spy.Calls["ContainOriginalVirtualIoBacking"]);Assert.Equal(1,native.ActiveDeviceLeaseCount);
                Assert.NotNull(spy.Inner.ResolveOriginalVirtualIoBackingContainment(exact));
                Assert.Equal(0x6A,foreign.Span[0]);slot.SetValue(sdkOwner,exact);
            }
            else if(damage is not null)
            {
                object rootOwner=Assert.Single(((IDictionary)spy.Inner.GetType().GetField("registry",flags)!.GetValue(spy.Inner)!).Values.Cast<object>());
                object backingOwner=Assert.Single(((IList)rootOwner.GetType().GetProperty("OriginalParentDevices")!.GetValue(rootOwner)!).Cast<object>());
                FieldInfo requestSlot=backingOwner.GetType().GetField("ContainmentRequest")!;
                FieldInfo closureSlot=backingOwner.GetType().GetField("Containment")!;
                var originalRequest=(HybridCPU.ExternalRuntime.Contracts.ExternalOriginalVirtualIoContainmentRequest)requestSlot.GetValue(backingOwner)!;
                var originalClosure=(HybridCPU.ExternalRuntime.Contracts.ExternalOriginalVirtualIoContainmentClosure)closureSlot.GetValue(backingOwner)!;
                if(damage=="receipt-copy")closureSlot.SetValue(backingOwner,originalClosure with {});
                else requestSlot.SetValue(backingOwner,null);
                Assert.Null(spy.Inner.ResolveOriginalVirtualIoBackingContainment(originalRequest));
                Assert.Null(spy.Inner.ContainOriginalVirtualIoBacking(originalRequest with {Operation=new(new(Guid.NewGuid()),new(998))}));
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(512UL,Used());
                Assert.Equal(1,native.ActiveDeviceLeaseCount);Assert.Equal(0x6A,foreign.Span[0]);
                requestSlot.SetValue(backingOwner,originalRequest);closureSlot.SetValue(backingOwner,originalClosure);
                Assert.Same(originalClosure,spy.Inner.ResolveOriginalVirtualIoBackingContainment(originalRequest));
            }
        }
        if(copyOwner)
        {
            property.SetValue(record,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null));
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False((bool)record.GetType().GetProperty("VirtualIoBindContained")!.GetValue(record)!);
            Assert.Equal(0UL,Used()); // Exact backing charge closed independently; invocation still pinned.
            property.SetValue(record,original);
        }
        KernelResult closed =kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Empty(records); // Exact canceled delivery and independent backing closure both finished.
        Assert.Equal(0UL,Used());Assert.Equal(0,native.ActiveDeviceLeaseCount);Assert.Equal(0,native.ActiveBindingCount);
        Assert.Equal(1,spy.Calls["BindOriginalVirtualIo"]);Assert.Equal(1,spy.Calls["ContainOriginalVirtualIoBacking"]);
        Assert.True(kernel.Processes.Resolve(service).IsSuccess);Assert.Equal(0x6A,foreign.Span[0]);
    }

    [Theory]
    [InlineData("deliver-first")] [InlineData("close-first")] [InlineData("charge-copy")]
    [InlineData("route-copy")]
    [InlineData("outcome-copy")] [InlineData("outcome-loss")]
    public void DeliveredBindRetiresOnlyAfterExactBackingClosureAndCharge(string mode)
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        ExactVirtualIoBackingConsumerTests.IOriginalBackingRuntime runtime =DispatchProxy.Create<ExactVirtualIoBackingConsumerTests.IOriginalBackingRuntime,BindLossSpy>();
        BindLossSpy spy =(BindLossSpy)(object)runtime;spy.Inner=new();spy.LoseBind=false;
        NeutralDomainRuntimeFacade native =new NeutralDomainRuntimeFacade();
        RuntimeKernel kernel =new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(native,new HybridCpuExecutableChildAdapter(spy.Inner,runtime)));
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel,2511,25110); ProcessHandle service =TestFixtures.Create(kernel,2512,25120).Handle;
        ProcessHandle admin =TestFixtures.Create(kernel,2513,25130).Handle;
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(service,32).Value!;foreign.Span[0]=0x6B;
        CapabilityId ac =kernel.MintCapability(new(25130),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(admin,ac,owner,"delivered IO Bind",[new(ServiceBudgetDimension.OwnedMemoryBytes,1024)]).Value!.ProcessBudget;
        OwnedBuffer<byte> region =kernel.AllocateBuffer<byte>(owner,512).Value!;
        CapabilityId create =kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain =kernel.CreateVirtualDomain(owner,create,new(1,4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess);
        PlatformDomainBinding root =kernel.QueryVirtualDomainPlatformBinding(owner,domain.Domain).Value;
        CapabilityId dc =kernel.MintCapability(process.DomainId,owner,ResourceKind.Device,"device/delivered-bind",CapabilityRights.Read|CapabilityRights.Write|CapabilityRights.Configure).Value!.CapabilityId;
        PlatformDeviceLease device =kernel.BindPlatformDevice(owner,root,dc,PlatformDeviceRights.Read|PlatformDeviceRights.Write).Value;
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"delivered IO Bind",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(owner,descriptor,[]).Value;
        RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        ChannelEndpointHandle channel =kernel.ResolveSession(owner,session).Value!.Channel;
        // No response waiter: final publication alone must keep the invocation.
        Assert.True(kernel.Send(owner,service,channel,IVirtualizationServiceProtocol.Message_BindIoAsync,
            new BindVirtualIoRequest(domain.Domain,device.LeaseId.Value,device.Generation.Value,VirtualIoAccess.Read|VirtualIoAccess.Write,512)).IsSuccess);
        EndpointSessionInvocationRegistry.OriginalPublication? publication=null;
        kernel.SessionInvocationSettlementReservedHook=()=>
        {
            kernel.SessionInvocationSettlementReservedHook=null;
            Assert.Equal(KernelError.InvalidTransition,host.ProcessNext().Error);
            object pending =host.GetType().GetField("_pendingResult",flags)!.GetValue(host)!;
            publication=(EndpointSessionInvocationRegistry.OriginalPublication)pending.GetType().GetField("Publication",flags)!.GetValue(pending)!;
        };
        Assert.True(host.ProcessNext().IsSuccess);
        object registry =kernel.GetType().GetField("_endpointSessionInvocationRegistry",flags)!.GetValue(kernel)!;
        IDictionary records =(IDictionary)registry.GetType().GetField("_ownedRecords",flags)!.GetValue(registry)!;
        IDictionary routes =(IDictionary)registry.GetType().GetField("_records",flags)!.GetValue(registry)!;
        object record =Assert.Single(records.Values.Cast<object>());
        object local =Assert.Single(((IDictionary)kernel.GetType().GetField("_virtualIo",flags)!.GetValue(kernel)!).Values.Cast<object>());
        VirtualIoBinding binding =(VirtualIoBinding)local.GetType().GetProperty("Binding",flags)!.GetValue(local)!;
        Assert.Equal(1024UL,Used());
        if(mode!="close-first" && !mode.StartsWith("outcome-",StringComparison.Ordinal))
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
                PropertyInfo field =local.GetType().GetProperty("BackingChargeOwner",flags)!;
                originalCharge=field.GetValue(local);field.SetValue(local,Clone(originalCharge!));
            }
            if(mode=="route-copy")
            {
                routeKey=Assert.Single(routes.Keys.Cast<object>());routes[routeKey]=Clone(record);
            }
        };
        Assert.Equal(mode is "deliver-first" or "close-first" or "outcome-copy" or "outcome-loss",kernel.CloseVirtualIo(binding).IsSuccess);
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
        if(mode.StartsWith("outcome-",StringComparison.Ordinal))
        {
            Assert.Single(records);Assert.Equal(512UL,Used());
            PropertyInfo slot=record.GetType().GetProperty("VirtualIoBindOutcome")!;
            object original=slot.GetValue(record)!;Assert.NotNull(original);
            slot.SetValue(record,mode=="outcome-loss"?null:Clone(original));
            record.GetType().GetProperty("VirtualIoBindContained")!.SetValue(record,true);
            Assert.True(kernel.ReceiveResponse(owner,channel).IsSuccess);
            Assert.Same(record,Assert.Single(records.Values.Cast<object>()));
            var invocation=(EndpointSessionInvocationHandle)record.GetType().GetProperty("Handle")!.GetValue(record)!;
            Assert.False(((EndpointSessionInvocationRegistry)registry).ConsumeVirtualIoAdmissionClosure(invocation,owner,local,record).IsSuccess);
            Assert.Equal(1,spy.Calls["CloseOriginalVirtualIo"]);Assert.Equal(512UL,Used());Assert.Equal(0x6B,foreign.Span[0]);
            slot.SetValue(record,original);
            Assert.True(((EndpointSessionInvocationRegistry)registry).ConsumeVirtualIoAdmissionClosure(invocation,owner,local,record).IsSuccess);
            Assert.Same(original,slot.GetValue(record));
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

