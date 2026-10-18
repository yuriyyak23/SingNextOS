using System.Collections;
using System.Reflection;
using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
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
    public class StageProvider : DispatchProxy
    {
        public IPlatformAuthorityProvider Inner=null!;
        public Action<MethodInfo,object?[]?>? After;
        public Func<PlatformProviderIncarnation,PlatformProviderIncarnation>? Observe;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            var raw=method!.Invoke(Inner,args);
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
            var raw=base.Invoke(method,args);
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
    public async Task OriginalCloseInvocationRetainsCompositeBackingAndIndependentMemory(string mode)
    {
        var native=new HybridCpuExternalRuntime();
        var external=DispatchProxy.Create<ExactVirtualIoBackingConsumerTests.IOriginalBackingRuntime,CloseSpy>();
        var spy=(CloseSpy)(object)external;spy.Inner=native;
        var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(),new HybridCpuExecutableChildAdapter(native,external)));
        var (process,owner)=TestFixtures.Create(kernel,3041,30410);var service=TestFixtures.Create(kernel,3042,30420).Handle;
        var admin=TestFixtures.Create(kernel,3043,30430).Handle;
        var foreign=kernel.AllocateBuffer<byte>(service,32).Value!;foreign.Span[0]=0x7A;
        var administration=kernel.MintCapability(new(30430),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(admin,administration,owner,"session-close-io",[new(ServiceBudgetDimension.OwnedMemoryBytes,mode=="transfer-charge-copy" ? 1026UL : 1024UL)]).Value!.ProcessBudget;
        var region=kernel.AllocateBuffer<byte>(owner,512).Value!;
        var create=kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var domain=kernel.CreateVirtualDomain(owner,create,new(1,4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess);
        var roots=(IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains",Private)!.GetValue(kernel.PlatformAuthority)!;
        var rootOwner=Assert.Single(roots.Values.Cast<object>());
        var root=(PlatformDomainBinding)rootOwner.GetType().GetProperty("Binding")!.GetValue(rootOwner)!;
        var source=kernel.MintCapability(process.DomainId,owner,ResourceKind.Device,"device:session-close-io",CapabilityRights.Read|CapabilityRights.Write|CapabilityRights.Configure).Value!.CapabilityId;
        var device=kernel.BindPlatformDevice(owner,root,source,PlatformDeviceRights.Read|PlatformDeviceRights.Write).Value!;
        var io=kernel.BindVirtualIo(owner,domain.Domain,device,new(PlatformDeviceRights.Read|PlatformDeviceRights.Write,512));Assert.True(io.IsSuccess,io.Message);
        var local=Assert.Single(((IDictionary)kernel.GetType().GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>());
        var protocol=VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"session-close-io",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(owner,descriptor,[]).Value;
        var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        var client=VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        if(mode=="transfer-charge-copy")
        {
            // This actual provider lacks independent snapshot release: whole-IO
            // closure must consume the read charge before retiring delivered custody.
            var reading=client.TransferIoAsync(new(io.Value.Id,io.Value.Generation,0,false,2,default));
            Assert.True(host.ProcessNext().IsSuccess);Assert.True((await reading).Completed);
            var invocations=kernel.GetType().GetField("_endpointSessionInvocationRegistry",Private)!.GetValue(kernel)!;
            var retained=(IDictionary)invocations.GetType().GetField("_ownedRecords",Private)!.GetValue(invocations)!;
            var invocation=Assert.Single(retained.Values.Cast<object>());
            var transfer=((IList)local.GetType().GetProperty("Transfers",Private)!.GetValue(local)!).Cast<object>().Single();
            var chargeField=transfer.GetType().GetField("ChargeOwner",Private)!;
            var originalCharge=chargeField.GetValue(transfer);
            chargeField.SetValue(transfer,Clone(originalCharge!));
            Assert.False(kernel.CloseVirtualIo(io.Value).IsSuccess);
            Assert.Same(invocation,Assert.Single(retained.Values.Cast<object>()));
            Assert.False((bool)invocation.GetType().GetProperty("VirtualIoTransferContained")!.GetValue(invocation)!);
            Assert.Equal(1026UL,Used());Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
            chargeField.SetValue(transfer,originalCharge);
            Assert.True(kernel.CloseVirtualIo(io.Value).IsSuccess);
            Assert.Empty(retained);Assert.Equal(512UL,Used());
            Assert.Equal(1,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
            Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0UL,Used());
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            Assert.Equal(0x7A,foreign.Span[0]);Assert.True(kernel.Processes.Resolve(service).IsSuccess);
            return;
        }
        var pending=client.CloseIoAsync(new(io.Value.Id,io.Value.Generation+(mode=="local-refusal" ? 1UL : 0))).AsTask();
        var registry=kernel.GetType().GetField("_endpointSessionInvocationRegistry",Private)!.GetValue(kernel)!;
        var owned=(IDictionary)registry.GetType().GetField("_ownedRecords",Private)!.GetValue(registry)!;
        var routes=(IDictionary)registry.GetType().GetField("_records",Private)!.GetValue(registry)!;
        var fact=Assert.Single(owned.Values.Cast<object>());
        object? routeKey=null;object? routed=null;object? charge=null;
        spy.Fault=mode;spy.BeforeEffect=mode=="before-effect";
        kernel.BeforeVirtualIoClosureLocalCommit=()=>
        {
            kernel.BeforeVirtualIoClosureLocalCommit=null;
            Assert.Equal(KernelError.InvalidTransition,host.ProcessNext().Error);
            var invocation=Assert.Single(owned.Values.Cast<object>());
            Assert.Same(local,invocation.GetType().GetProperty("OriginalVirtualIoClose")!.GetValue(invocation));
            if(mode=="owner-copy")invocation.GetType().GetProperty("OriginalVirtualIoClose")!.SetValue(invocation,Clone(local));
            if(mode is "route-loss" or "route-copy")
            {
                routeKey=Assert.Single(routes.Keys.Cast<object>());routed=routes[routeKey];
                if(mode=="route-loss")routes.Remove(routeKey);else routes[routeKey]=Clone(routed!);
            }
            if(mode=="charge-copy")
            {
                var property=local.GetType().GetProperty("BackingChargeOwner",Private)!;charge=property.GetValue(local);property.SetValue(local,Clone(charge!));
            }
            if(mode=="source-revision")
            {
                var state=local.GetType().GetProperty("State",Private)!;state.SetValue(local,state.GetValue(local));
            }
        };
        if(mode=="publication-loss")kernel.SessionInvocationSettlementReservedHook=()=>
        {
            kernel.SessionInvocationSettlementReservedHook=null;
            throw new IOException("Close response lost after composite closure.");
        };
        if(mode=="publication-loss")Assert.Throws<IOException>(()=>host.ProcessNext());
        else Assert.Equal(mode=="io-close-lost",host.ProcessNext().IsSuccess);
        if(mode=="io-close-lost"){await pending;Assert.Empty(owned);}
        else if(mode=="local-refusal"){await Assert.ThrowsAnyAsync<Exception>(async()=>await pending);Assert.Empty(owned);}
        else Assert.Same(fact,Assert.Single(owned.Values.Cast<object>()));
        if(mode=="local-refusal")Assert.False((bool)fact.GetType().GetProperty("ServiceAccepted")!.GetValue(fact)!);
        else Assert.NotNull(fact.GetType().GetProperty("OriginalVirtualIoClose")!.GetValue(fact));
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        var partial=mode is "owner-copy" or "route-loss" or "route-copy";
        var completed=mode is "io-close-lost" or "publication-loss";
        Assert.Equal(partial || completed ? 512UL : 1024UL,Used());
        if(routeKey is not null)routes[routeKey]=routed;
        if(mode=="owner-copy")fact.GetType().GetProperty("OriginalVirtualIoClose")!.SetValue(fact,local);
        if(mode=="charge-copy")local.GetType().GetProperty("BackingChargeOwner",Private)!.SetValue(local,charge);
        kernel.BeforeVirtualIoClosureLocalCommit=null;
        spy.Fault="exact";spy.BeforeEffect=false;
        _=kernel.RevokeCapability(source); // Permission revocation and cascade closure are independent results.
        Assert.False(kernel.ValidateCapability(owner,source,CapabilityRights.Read).IsSuccess);
        if(mode=="io-close-lost")
        {
            var completedAgain=client.CloseIoAsync(new(io.Value.Id,io.Value.Generation));
            Assert.True(host.ProcessNext().IsSuccess);await completedAgain;
            Assert.Equal(512UL,Used());Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
        }
        if(mode is "before-effect" or "io-close-copy")
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1024UL,Used());
        }
        else
        {
            var closed=kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
            Assert.Equal(0UL,Used());Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        }
        if(mode=="io-close-lost")await pending;else await Assert.ThrowsAnyAsync<Exception>(async()=>await pending);
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
    [InlineData("transfer-native-noeffect")]
    public async Task LocalBindRefusalClosesOnlyItsOwnInvocationWithoutContainingUnknownOriginal(string mode)
    {
        var native=new HybridCpuExternalRuntime();
        var external=DispatchProxy.Create<ExactVirtualIoBackingConsumerTests.IOriginalBackingRuntime,CloseSpy>();
        var spy=(CloseSpy)(object)external;spy.Inner=native;
        IPlatformAuthorityProvider provider=new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(),new HybridCpuExecutableChildAdapter(native,external));
        StageProvider? stage=null;
        var staged=mode.StartsWith("transfer-stage-",StringComparison.Ordinal);
        var nativeNoEffect=mode=="transfer-native-noeffect";
        if(staged)
        {
            var proxy=DispatchProxy.Create<IStageProvider,StageProvider>();stage=(StageProvider)(object)proxy;stage.Inner=provider;provider=proxy;
        }
        var kernel=new RuntimeKernel(provider);
        var (process,owner)=TestFixtures.Create(kernel,3051,30510);var service=TestFixtures.Create(kernel,3052,30520).Handle;
        var admin=TestFixtures.Create(kernel,3053,30530).Handle;
        var foreign=kernel.AllocateBuffer<byte>(service,32).Value!;foreign.Span[0]=0x7B;
        var administration=kernel.MintCapability(new(30530),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(admin,administration,owner,"local-bind-refusal",[new(ServiceBudgetDimension.OwnedMemoryBytes,mode=="budget" ? 512UL : staged || nativeNoEffect ? 1026UL : 1024UL)]).Value!.ProcessBudget;
        var region=kernel.AllocateBuffer<byte>(owner,512).Value!;
        var create=kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var domain=kernel.CreateVirtualDomain(owner,create,new(1,4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess);
        var roots=(IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains",Private)!.GetValue(kernel.PlatformAuthority)!;
        var rootOwner=Assert.Single(roots.Values.Cast<object>());
        var root=(PlatformDomainBinding)rootOwner.GetType().GetProperty("Binding")!.GetValue(rootOwner)!;
        var source=kernel.MintCapability(process.DomainId,owner,ResourceKind.Device,"device:local-bind-refusal",CapabilityRights.Read|CapabilityRights.Write|CapabilityRights.Configure).Value!.CapabilityId;
        var device=kernel.BindPlatformDevice(owner,root,source,PlatformDeviceRights.Read|PlatformDeviceRights.Write).Value!;
        var transferMode=mode.StartsWith("transfer-",StringComparison.Ordinal);
        VirtualIoBinding io=default;
        if(transferMode)
        {
            var admitted=kernel.BindVirtualIo(owner,domain.Domain,device,new(mode=="transfer-rights" ? PlatformDeviceRights.Read : PlatformDeviceRights.Read|PlatformDeviceRights.Write,512));
            Assert.True(admitted.IsSuccess,admitted.Message);io=admitted.Value;
            if(mode=="transfer-closed")Assert.True(kernel.CloseVirtualIo(io).IsSuccess);
        }
        var protocol=VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"local-bind-refusal",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(owner,descriptor,[]).Value;
        var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        var client=VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        object? stagedFact=null;object? originalCharge=null;object? originalInvocation=null;object? invocationRecord=null;
        if(stage is not null)stage.Observe=incarnation=>
        {
            var locals=((IDictionary)kernel.GetType().GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>().ToArray();
            if(locals.Length!=1)return incarnation;
            var facts=((IList)locals[0].GetType().GetProperty("Transfers",Private)!.GetValue(locals[0])!).Cast<object>().ToArray();
            if(facts.Length!=1)return incarnation;
            var candidate=facts[0];
            if(mode=="transfer-stage-prepare")return new(0);
            if(candidate.GetType().GetField("Platform",Private)!.GetValue(candidate) is null || stagedFact is not null)return incarnation;
            stagedFact=candidate;
            var registry=kernel.GetType().GetField("_endpointSessionInvocationRegistry",Private)!.GetValue(kernel)!;
            invocationRecord=Assert.Single(((IDictionary)registry.GetType().GetField("_ownedRecords",Private)!.GetValue(registry)!).Values.Cast<object>());
            if(mode=="transfer-stage-owner")
            {
                var property=invocationRecord.GetType().GetProperty("OriginalVirtualIoTransfer")!;
                originalInvocation=property.GetValue(invocationRecord);property.SetValue(invocationRecord,Clone(originalInvocation!));
            }
            if(mode=="transfer-stage-charge")
            {
                var field=candidate.GetType().GetField("ChargeOwner",Private)!;originalCharge=field.GetValue(candidate);field.SetValue(candidate,Clone(originalCharge!));
            }
            return new(incarnation.Value+1);
        };
        if(mode=="source-revoked")_=kernel.RevokeCapability(source);
        spy.BeforeBind=mode=="bind-delivery-loss";spy.Fault=mode;
        var request=new BindVirtualIoRequest(mode=="stale-domain" ? domain.Domain with {Generation=new(domain.Domain.Generation.Value+1)} : domain.Domain,
            device.LeaseId.Value,device.Generation.Value,VirtualIoAccess.Read|VirtualIoAccess.Write,512);
        var transferReply=transferMode ? client.TransferIoAsync(new(io.Id,io.Generation,nativeNoEffect ? 512UL : 0,mode is "transfer-input" or "transfer-rights",2,
            mode=="transfer-rights" ? new SingPlus.Sip.Native.BoundedBytes(new byte[]{1,2}) : default)).AsTask() : null;
        Task reply=transferReply is not null ? transferReply : client.BindIoAsync(request).AsTask();
        var registry=kernel.GetType().GetField("_endpointSessionInvocationRegistry",Private)!.GetValue(kernel)!;
        var owned=(IDictionary)registry.GetType().GetField("_ownedRecords",Private)!.GetValue(registry)!;
        var fact=Assert.Single(owned.Values.Cast<object>());
        Assert.Equal(nativeNoEffect,host.ProcessNext().IsSuccess);
        if(nativeNoEffect){var result=await transferReply!;Assert.True(result.NoEffect);Assert.False(result.Completed);}
        else await Assert.ThrowsAnyAsync<Exception>(async()=>await reply);

        var unknown=mode is "bind-delivery-loss" or "io-bind-query-throw";
        Assert.Equal(mode!="source-revoked",(bool)fact.GetType().GetProperty("ServiceAccepted")!.GetValue(fact)!);
        Assert.Equal(!unknown && !staged && !nativeNoEffect,(bool)fact.GetType().GetProperty(transferMode ? "VirtualIoTransferNeverInvoked" : "VirtualIoBindNeverInvoked")!.GetValue(fact)!);
        Assert.Equal(unknown || staged || nativeNoEffect,fact.GetType().GetProperty(transferMode ? "OriginalVirtualIoTransfer" : "OriginalVirtualIoBind")!.GetValue(fact) is not null);
        if((bool)fact.GetType().GetProperty(transferMode ? "VirtualIoTransferNoEffect" : "VirtualIoBindNeverInvoked")!.GetValue(fact)! ||
            transferMode && (bool)fact.GetType().GetProperty("VirtualIoTransferNeverInvoked")!.GetValue(fact)!)Assert.Empty(owned);
        Assert.Equal(mode=="transfer-stage-charge" ? 1026UL : unknown || transferMode && mode!="transfer-closed" ? 1024UL : 512UL,Used());
        if(stage is not null || nativeNoEffect)
        {
            if(stage is not null)stage.Observe=null;
            if(mode=="transfer-stage-owner")invocationRecord!.GetType().GetProperty("OriginalVirtualIoTransfer")!.SetValue(invocationRecord,originalInvocation);
            if(mode=="transfer-stage-charge")stagedFact!.GetType().GetField("ChargeOwner",Private)!.SetValue(stagedFact,originalCharge);
            stagedFact ??=fact.GetType().GetProperty("OriginalVirtualIoTransfer")!.GetValue(fact);
            var originalRequest=(KernelVirtualIoTransferRequest)stagedFact!.GetType().GetField("Request",Private)!.GetValue(stagedFact)!;
            Assert.Equal(nativeNoEffect,kernel.TransferVirtualIo(owner,originalRequest).IsSuccess); // Cleanup-only, no new transfer.
            Assert.Equal(1024UL,Used());
            Assert.True((bool)fact.GetType().GetProperty("VirtualIoTransferNoEffect")!.GetValue(fact)!);
            Assert.Equal(0,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
            if(stagedFact.GetType().GetField("Platform",Private)!.GetValue(stagedFact) is PlatformOriginalVirtualIoTransferRequest platform)
            {
                var local=Assert.Single(((IDictionary)kernel.GetType().GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>());
                var binding=(PlatformVirtualIoBinding)local.GetType().GetProperty("Platform",Private)!.GetValue(local)!;
                Assert.False(kernel.PlatformAuthority.InvokeVirtualIoTransfer(binding,platform).IsSuccess);
                var called=false;
                Assert.False(kernel.PlatformAuthority.ConsumeUninvokedVirtualIoTransfer(binding,(PlatformOriginalVirtualIoTransferRequest)Clone(platform),()=>{called=true;return KernelResult.Ok();}).IsSuccess);
                Assert.False(called);
            }
        }
        Assert.Equal(mode=="io-bind-query-throw" || transferMode ? 1 : 0,spy.Calls.GetValueOrDefault("BindOriginalVirtualIo"));
        Assert.Equal(nativeNoEffect ? 1 : 0,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
        if(unknown)
        {
            // A different request cannot re-reserve or bypass the unresolved owner.
            var again=client.BindIoAsync(request);Assert.False(host.ProcessNext().IsSuccess);
            await Assert.ThrowsAnyAsync<Exception>(async()=>await again);
            Assert.Equal(1024UL,Used());
            Assert.DoesNotContain(owned.Values.Cast<object>(),f=>(bool)f.GetType().GetProperty("VirtualIoBindNeverInvoked")!.GetValue(f)!);
            if(mode=="io-bind-query-throw")spy.Fault="exact";
            spy.BeforeBind=false;
        }
        var closed=kernel.TerminateProcess(owner);
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
    [InlineData("copy")] [InlineData("charge-copy")] [InlineData("release-request-loss")] [InlineData("sdk-transfer-raw-loss")] [InlineData("provider-transfer-raw-loss")] [InlineData("bridge-publication-copy")] [InlineData("bridge-publication-history")] [InlineData("bridge-snapshot-history")] [InlineData("bridge-snapshot-copy")] [InlineData("bridge-snapshot-transfer-before")] [InlineData("bridge-snapshot-transfer-after")] [InlineData("release-close-reentry")] [InlineData("release-terminate-reentry")] [InlineData("release-terminate-reentry-query-loss")]
    [InlineData("before-release-effect")] [InlineData("release-source-fresh")] [InlineData("release-source-throw")]
    [InlineData("canceled-read")] [InlineData("canceled-read-release-query")]
    [InlineData("publication-drain")] [InlineData("write-before-close")]
    [InlineData("publication-close")] [InlineData("publication-channel-close")]
    [InlineData("publication-route-loss")] [InlineData("publication-route-service-exit")]
    public async Task CompletedReadReleasesOnlyOriginalNativeSnapshotAfterSessionCopy(string mode)
    {
        var external=DispatchProxy.Create<IReleaseRuntime,CloseSpy>();var spy=(CloseSpy)(object)external;spy.Inner=new();
        var rootProxy=DispatchProxy.Create<ExactArtifactReleaseConsumerTests.IOriginalRootRuntime,ExactArtifactReleaseConsumerTests.RootSpy>();
        var rootSpy=(ExactArtifactReleaseConsumerTests.RootSpy)(object)rootProxy;rootSpy.Inner=spy.Inner;
        var sdk=new HybridCpuExecutableChildAdapter(rootProxy,external);
        var provider=new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(),sdk);
        IPlatformAuthorityProvider authority=provider;StageProvider? stage=null;
        if(mode.StartsWith("bridge-",StringComparison.Ordinal))
        {
            var proxy=DispatchProxy.Create<IReadStageProvider,StageProvider>();stage=(StageProvider)(object)proxy;stage.Inner=provider;authority=proxy;
        }
        var routePublication=mode.StartsWith("publication-route-",StringComparison.Ordinal);
        var admissionClock=routePublication?new AdmissionClock():null;
        var kernel=new RuntimeKernel(authority,admissionClock);
        var (process,owner)=TestFixtures.Create(kernel,3061,30610);var service=TestFixtures.Create(kernel,3062,30620).Handle;
        var admin=TestFixtures.Create(kernel,3063,30630).Handle;
        var foreignOwner=mode=="publication-route-service-exit"?TestFixtures.Create(kernel,3064,30640).Handle:service;
        var foreign=kernel.AllocateBuffer<byte>(foreignOwner,32).Value!;foreign.Span[0]=0x7C;
        var administration=kernel.MintCapability(new(30630),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(admin,administration,owner,"read-snapshot-release",[new(ServiceBudgetDimension.OwnedMemoryBytes,1026)]).Value!.ProcessBudget;
        var region=kernel.AllocateBuffer<byte>(owner,512).Value!;
        var create=kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var domain=kernel.CreateVirtualDomain(owner,create,new(1,4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess);
        var rootOwner=Assert.Single(((IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains",Private)!.GetValue(kernel.PlatformAuthority)!).Values.Cast<object>());
        var root=(PlatformDomainBinding)rootOwner.GetType().GetProperty("Binding")!.GetValue(rootOwner)!;
        var source=kernel.MintCapability(process.DomainId,owner,ResourceKind.Device,"device:read-release",CapabilityRights.Read|CapabilityRights.Write|CapabilityRights.Configure).Value!.CapabilityId;
        var device=kernel.BindPlatformDevice(owner,root,source,PlatformDeviceRights.Read|PlatformDeviceRights.Write).Value!;
        var io=kernel.BindVirtualIo(owner,domain.Domain,device,new(PlatformDeviceRights.Read|PlatformDeviceRights.Write,512)).Value;
        Assert.True(kernel.TransferVirtualIo(owner,new(io,0,true,2,new byte[]{71,73})).IsSuccess);
        var protocol=VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"read-release",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(owner,descriptor,[],lifetime:admissionClock is null?null:TimeSpan.FromMinutes(1)).Value;
        var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        var client=VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        var generatedWrite=mode=="write-before-close";
        if(generatedWrite)
        {
            var writing=client.TransferIoAsync(new(io.Id,io.Generation,2,true,1,new SingPlus.Sip.Native.BoundedBytes(new byte[]{83})));
            Assert.True(host.ProcessNext().IsSuccess);Assert.True((await writing).Completed);
        }
        spy.ReleaseReplyLoss=mode is "reply-loss" or "query-loss" or "release-terminate-reentry-query-loss";spy.ReleaseQueryLoss=mode is "query-loss" or "release-terminate-reentry-query-loss";spy.ReleaseCopy=mode=="copy";
        spy.ReleaseBeforeEffect=mode=="before-release-effect";
        object? readFact=null;object? originalCharge=null;
        object? releaseOwner=null,originalReleaseRequest=null;
        object? transferRawOwner=null,originalTransferRaw=null;
        bool closeReentryAttempted=false,closeReentryDenied=false,closeReentryRevisionStable=false,closeReentryLocksFree=false;
        bool terminateReentryAttempted=false,terminateReentryDenied=false,terminateReentryLocksFree=false;
        var sourceCallbacks=0;var sourceLocksFree=false;
        if(mode.StartsWith("release-source-",StringComparison.Ordinal))rootSpy.BeforeQuery=()=>
        {
            var ioOwner=Assert.Single(((IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("originalIo",Private)!.GetValue(sdk)!).Values.Cast<object>());
            var fact=((IEnumerable)ioOwner.GetType().GetProperty("Transfers")!.GetValue(ioOwner)!).Cast<object>().Last();
            var field=fact.GetType().GetField("ReleaseRequest")!;
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
                var local=Assert.Single(((IDictionary)kernel.GetType().GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>());
                var revision=local.GetType().GetProperty("SourceRevision",Private)!;var before=revision.GetValue(local);
                closeReentryAttempted=true;
                closeReentryLocksFree=!Monitor.IsEntered(kernel.GetType().GetField("_virtualIoGate",Private)!.GetValue(kernel)!);
                closeReentryDenied=!kernel.CloseVirtualIo(io).IsSuccess;
                closeReentryRevisionStable=Equals(before,revision.GetValue(local));
            }
            if(mode.EndsWith("-transfer-raw-loss",StringComparison.Ordinal))
            {
                object holder=mode.StartsWith("sdk",StringComparison.Ordinal)?sdk:provider;
                var registry=(IDictionary)holder.GetType().GetField(ReferenceEquals(holder,sdk)?"originalIo":"_originalProviderVirtualIo",Private)!.GetValue(holder)!;
                var ioOwner=Assert.Single(registry.Values.Cast<object>());
                transferRawOwner=((IEnumerable)ioOwner.GetType().GetProperty("Transfers")!.GetValue(ioOwner)!).Cast<object>().Last();
                var field=transferRawOwner.GetType().GetField("Raw")!;originalTransferRaw=field.GetValue(transferRawOwner);field.SetValue(transferRawOwner,null);
            }
            if(mode=="release-request-loss")
            {
                var ioOwner=Assert.Single(((IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("originalIo",Private)!.GetValue(sdk)!).Values.Cast<object>());
                releaseOwner=((IEnumerable)ioOwner.GetType().GetProperty("Transfers")!.GetValue(ioOwner)!).Cast<object>().Last();
                var requestField=releaseOwner.GetType().GetField("ReleaseRequest")!;
                originalReleaseRequest=requestField.GetValue(releaseOwner);
                requestField.SetValue(releaseOwner,null);
            }
            if(mode=="charge-copy")
            {
                var local=Assert.Single(((IDictionary)kernel.GetType().GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>());
                readFact=((IList)local.GetType().GetProperty("Transfers",Private)!.GetValue(local)!).Cast<object>().Last();
                var field=readFact.GetType().GetField("ChargeOwner",Private)!;originalCharge=field.GetValue(readFact);field.SetValue(readFact,Clone(originalCharge!));
            }
        };

        EndpointSessionInvocationRegistry.OriginalPublication? publishedOriginal=null;
        if(mode=="exact")kernel.SessionInvocationSettlementReservedHook=()=>
        {
            kernel.SessionInvocationSettlementReservedHook=null;
            var pending=host.GetType().GetField("_pendingResult",Private)!.GetValue(host)!;
            publishedOriginal=(EndpointSessionInvocationRegistry.OriginalPublication)pending.GetType().GetField("Publication",Private)!.GetValue(pending)!;
        };
        object? bridgeTransfer=null,bridgeRaw=null;IList? bridgeHistory=null;object[]? bridgeInventory=null;
        var bridgeObserved=0;var bridgeLocksFree=false;var queryReturned=false;
        var bridgeSnapshot=mode.StartsWith("bridge-snapshot-",StringComparison.Ordinal);
        var bridgePreRelease=mode=="bridge-snapshot-transfer-before";
        var bridgeRawFieldName=bridgeSnapshot && !mode.Contains("-transfer-",StringComparison.Ordinal)?"SnapshotReleaseRaw":"Raw";
        if(stage is not null)
        {
            stage.After=(method,args)=>{if(bridgePreRelease ? method.Name=="get_SupportsOriginalVirtualIoSnapshotRelease" : method.Name==(bridgeSnapshot?"ResolveOriginalVirtualIoSnapshotRelease":"ResolveOriginalVirtualIoTransfer") && args![0] is PlatformOriginalVirtualIoTransferRequest {Write:false})queryReturned=true;};
            stage.Observe=incarnation=>
            {
                if(!queryReturned || bridgeObserved!=0)return incarnation;
                bridgeObserved++;
                bridgeLocksFree=!Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate",Private)!.GetValue(kernel.PlatformAuthority)!);
                var ioOwner=Assert.Single(((IDictionary)typeof(PlatformAuthorityBridge).GetField("_virtualIoBindings",Private)!.GetValue(kernel.PlatformAuthority)!).Values.Cast<object>());
                bridgeHistory=(IList)ioOwner.GetType().GetProperty("Transfers")!.GetValue(ioOwner)!;
                bridgeTransfer=bridgeHistory.Cast<object>().Last();
                var field=bridgeTransfer.GetType().GetField(bridgeRawFieldName)!;bridgeRaw=field.GetValue(bridgeTransfer)!;
                if(mode.EndsWith("-copy",StringComparison.Ordinal) || mode.Contains("-transfer-",StringComparison.Ordinal))field.SetValue(bridgeTransfer,Clone(bridgeRaw));
                else {bridgeInventory=bridgeHistory.Cast<object>().ToArray();bridgeHistory.Clear();}
                return incarnation;
            };
        }
        var canceledRead=mode.StartsWith("canceled-read",StringComparison.Ordinal);
        if(canceledRead)spy.Fault="kernel-read-query-loss";
        var read=client.TransferIoAsync(new(io.Id,io.Generation,0,false,2,default));
        var invocationRegistry=kernel.GetType().GetField("_endpointSessionInvocationRegistry",Private)!.GetValue(kernel)!;
        var invocationOwners=(IDictionary)invocationRegistry.GetType().GetField("_ownedRecords",Private)!.GetValue(invocationRegistry)!;
        object ReadFact()
        {
            var local=Assert.Single(((IDictionary)kernel.GetType().GetField("_virtualIo",Private)!.GetValue(kernel)!).Values.Cast<object>());
            return ((IList)local.GetType().GetProperty("Transfers",Private)!.GetValue(local)!).Cast<object>().Last();
        }
        if(routePublication)
        {
            var routes=(IDictionary)invocationRegistry.GetType().GetField("_records",Private)!.GetValue(invocationRegistry)!;
            var key=Assert.Single(routes.Keys.Cast<object>());var original=routes[key]!;var observed=0;
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
                var exited=kernel.TerminateProcess(service);Assert.True(exited.IsSuccess,exited.Message);
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
            var retry=host.AdvancePendingResult();Assert.True(retry.IsSuccess,retry.Message);
            var delivered=await read;Assert.True(delivered.Completed);Assert.Equal(new byte[]{71,73},delivered.Data.ToArray());
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
            var retained=ReadFact();Assert.NotNull(retained.GetType().GetField("SessionResponse",Private)!.GetValue(retained));
            Assert.Equal(1024UL,Used());Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));
            var ended=kernel.TerminateProcess(owner);Assert.True(ended.IsSuccess,ended.Message);
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
            var correlation=kernel.GetType().GetField("_requestResponseCorrelationGate",Private)!.GetValue(kernel)!;
            ResponseRegistry.OriginalPublication? originalCompletion=null;
            lock(correlation)
            {
                Assert.True(host.ProcessNext().IsSuccess);
                var invocation=Assert.Single(invocationOwners.Values.Cast<object>());
                originalCompletion=(ResponseRegistry.OriginalPublication)invocation.GetType().GetProperty("OriginalResponse")!.GetValue(invocation)!;
                if(mode=="publication-close")Assert.True(kernel.CloseSession(owner,session).IsSuccess);
                else Assert.True(kernel.Channels.Close(kernel.ResolveSession(owner,session).Value!.Channel).IsSuccess);
            }
            var delivered=await read;
            Assert.True(delivered.Completed);Assert.Equal(new byte[]{71,73},delivered.Data.ToArray());
            var originalRaw=originalCompletion.Owner.ReadPublication(originalCompletion).Value!;
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
        var first=host.ProcessNext();
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
            var retainedResponseFact=ReadFact();
            var teardown=kernel.ObserveProcessTeardown(owner);Assert.True(teardown.IsSuccess,teardown.Message);
            Assert.True(teardown.Value.LocalAuthorizationRevoked);Assert.True(teardown.Value.ChannelsClosed);Assert.False(teardown.Value.IsComplete);
            Assert.False(kernel.TransferVirtualIo(owner,new(io,0,true,1,[0xEE])).IsSuccess);
            Assert.Equal(2,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
            spy.ReleaseQueryLoss=false;spy.ReleaseReplyLoss=false;
            var ended=kernel.TerminateProcess(owner);Assert.True(ended.IsSuccess,ended.Message);
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
            var sdkRequest=(NeutralOriginalVirtualIoTransferRequest)releaseOwner!.GetType().GetField("Request")!.GetValue(releaseOwner)!;
            var sdkReceipt=(NeutralOriginalVirtualIoTransferReceipt)releaseOwner.GetType().GetField("Published")!.GetValue(releaseOwner)!;
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
            if(bridgeInventory is not null)foreach(var original in bridgeInventory)bridgeHistory!.Add(original);
            else bridgeTransfer!.GetType().GetField(bridgeRawFieldName)!.SetValue(bridgeTransfer,bridgeRaw);
            Assert.True(host.AdvancePendingResult().IsSuccess);
            Assert.Equal(2,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));
            Assert.Equal(1024UL,Used());Assert.Empty(invocationOwners);
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0UL,Used());
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);Assert.Equal(0x7C,foreign.Span[0]);return;
        }
        SingPlus.Sip.Native.BoundedBytes detached=default;
        if(first.IsSuccess){var response=await read;detached=response.Data;Assert.Equal(new byte[]{71,73},response.Data.ToArray());
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
            foreach(var holder in new object[]{sdk,provider})
            {
                var registry=(IDictionary)holder.GetType().GetField(ReferenceEquals(holder,sdk)?"originalIo":"_originalProviderVirtualIo",Private)!.GetValue(holder)!;
                var ioOwner=Assert.Single(registry.Values.Cast<object>());
                var fact=((IEnumerable)ioOwner.GetType().GetProperty("Transfers")!.GetValue(ioOwner)!).Cast<object>().Last();
                var transferRequest=fact.GetType().GetField("Request")!.GetValue(fact)!;
                var transferField=fact.GetType().GetField("Published")!;var exactTransfer=transferField.GetValue(fact)!;
                var transferRawField=fact.GetType().GetField("Raw")!;var exactRaw=transferRawField.GetValue(fact)!;
                var transferResolver=holder.GetType().GetMethod("ResolveOriginalVirtualIoTransfer")!;
                bool ResolveTransfer(){var resolved=transferResolver.Invoke(holder,[transferRequest])!;return (bool)resolved.GetType().GetProperty("IsSuccess")!.GetValue(resolved)!;}
                transferRawField.SetValue(fact,null);Assert.False(ResolveTransfer());
                transferRawField.SetValue(fact,Clone(exactRaw));Assert.False(ResolveTransfer());
                transferRawField.SetValue(fact,exactRaw);Assert.True(ResolveTransfer());
                transferField.SetValue(fact,Clone(exactTransfer));Assert.False(ResolveTransfer());
                transferField.SetValue(fact,exactTransfer);Assert.True(ResolveTransfer());
                Assert.Equal(2,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
                var rawField=fact.GetType().GetField("ReleaseRaw")!;
                var retained=rawField.GetValue(fact)!;
                var request=fact.GetType().GetField("Request")!.GetValue(fact)!;
                var transfer=fact.GetType().GetField("Published")!.GetValue(fact)!;
                var resolver=holder.GetType().GetMethod("ResolveOriginalVirtualIoSnapshotRelease")!;
                bool Resolve(){var result=resolver.Invoke(holder,[request,transfer])!;return (bool)result.GetType().GetProperty("IsSuccess")!.GetValue(result)!;}
                rawField.SetValue(fact,Clone(retained)); Assert.False(Resolve());
                rawField.SetValue(fact,retained); Assert.True(Resolve());
                if(ReferenceEquals(holder,sdk))
                {
                    var field=fact.GetType().GetField("ReleaseRequest")!;var original=field.GetValue(fact);
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
            var closing=client.CloseIoAsync(new(io.Id,io.Generation));Assert.True(host.ProcessNext().IsSuccess);await closing;
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
        var copied=(ExternalOriginalVirtualIoSnapshotReleaseRequest)Clone(spy.ReleaseRequest!);
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
        if(mode=="release-request-loss")
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
            var closed=kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
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
        var external=DispatchProxy.Create<IReleaseRuntime,CloseSpy>();var spy=(CloseSpy)(object)external;spy.Inner=new();
        var sdk=new HybridCpuExecutableChildAdapter(spy.Inner,external);
        var model=new NeutralDomainRuntimeFacade();var provider=new HybridCpuPlatformAuthorityProvider(model,sdk);
        var kernel=new RuntimeKernel(provider);
        var (process,owner)=TestFixtures.Create(kernel,3071,30710);var service=TestFixtures.Create(kernel,3072,30720).Handle;
        var admin=TestFixtures.Create(kernel,3073,30730).Handle;
        var foreign=kernel.AllocateBuffer<byte>(service,32).Value!;foreign.Span[0]=0x7C;
        var administration=kernel.MintCapability(new(30730),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(admin,administration,owner,"independent-read-release",[new(ServiceBudgetDimension.OwnedMemoryBytes,peerUnknown?1539UL:1538UL)]).Value!.ProcessBudget;
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        var region=kernel.AllocateBuffer<byte>(owner,512).Value!;
        var create=kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var domain=kernel.CreateVirtualDomain(owner,create,new(1,4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess);
        var rootOwner=Assert.Single(((IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains",Private)!.GetValue(kernel.PlatformAuthority)!).Values.Cast<object>());
        var root=(PlatformDomainBinding)rootOwner.GetType().GetProperty("Binding")!.GetValue(rootOwner)!;
        VirtualIoBinding Bind(string id)
        {
            var cap=kernel.MintCapability(process.DomainId,owner,ResourceKind.Device,id,CapabilityRights.Read|CapabilityRights.Write|CapabilityRights.Configure).Value!.CapabilityId;
            var device=kernel.BindPlatformDevice(owner,root,cap,PlatformDeviceRights.Read|PlatformDeviceRights.Write).Value!;
            return kernel.BindVirtualIo(owner,domain.Domain,device,new(PlatformDeviceRights.Read|PlatformDeviceRights.Write,512)).Value;
        }
        var io=Bind("device:release-first");var peer=Bind("device:release-peer");Assert.Equal(1536UL,Used());
        Assert.True(kernel.TransferVirtualIo(owner,new(io,0,true,2,[71,73])).IsSuccess);
        if(peerUnknown)Assert.True(kernel.TransferVirtualIo(owner,new(peer,0,true,1,[0x6B])).IsSuccess);
        KernelVirtualIoTransferRequest? peerOriginal=null;
        var protocol=VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"independent-read-release",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(owner,descriptor,[]).Value;var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        var client=VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        var callbacks=0;bool sameCloseDenied=false,sameTransferDenied=false,peerWrote=false,locksFree=false;string? peerFailure=null;
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
            var write=kernel.TransferVirtualIo(owner,peerOriginal ?? new(peer,0,true,1,[0x6B]));peerWrote=write.IsSuccess;peerFailure=write.Message;
            during=Used();
        };
        var read=client.TransferIoAsync(new(io.Id,io.Generation,0,false,2,default));
        var handled=host.ProcessNext();Assert.True(handled.IsSuccess,handled.Message);
        var response=await read;spy.After=null;spy.Fault="exact";
        Assert.Equal(1,callbacks);Assert.True(locksFree);Assert.True(sameCloseDenied);Assert.True(sameTransferDenied);if(peerUnknown)Assert.False(peerWrote);else Assert.True(peerWrote,peerFailure);
        Assert.Equal(peerUnknown?1539UL:1538UL,during);Assert.Equal(peerUnknown?1537UL:1536UL,Used());
        Assert.Equal(peerUnknown?4:3,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));
        Assert.Equal(0,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));Assert.Equal(new byte[]{71,73},response.Data.ToArray());
        Assert.True(kernel.CloseVirtualIo(io).IsSuccess);Assert.Equal(peerUnknown?1025UL:1024UL,Used());
        var peerRead=kernel.TransferVirtualIo(owner,peerOriginal ?? new(peer,0,false,1));Assert.True(peerRead.IsSuccess,peerRead.Message);Assert.Equal(new byte[]{0x6B},peerRead.Value!.CopyReadData());
        Assert.Equal(1025UL,Used());Assert.True(kernel.CloseVirtualIo(peer).IsSuccess);Assert.Equal(512UL,Used());
        Assert.Equal(4,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));Assert.Equal(2,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0UL,Used());
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        Assert.Equal(0,model.ActiveDeviceLeaseCount);Assert.Equal(0,model.ActiveBindingCount);
        Assert.Equal(new byte[]{71,73},response.Data.ToArray());Assert.True(foreign.IsValid);Assert.Equal(0x7C,foreign.Span[0]);
    }
    private static object Clone(object value)=>typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(value,null)!;
}








