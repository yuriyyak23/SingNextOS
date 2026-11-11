using System.Collections;
using System.Reflection;
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

public sealed class ArtifactReleaseIoAdmissionTests
{
    // Current compiler HCEXE12316 requires a fresh bounded16KiB code admission; private quota stays16KiB.
    private const ulong CodeCharge = 16384UL, ArtifactCharge = CodeCharge + 16384UL;

    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    public interface IRuntime : ExactNestedArtifactConsumerTests.IOriginalNestedStartRuntime,ExactVirtualIoBackingConsumerTests.IOriginalBackingRuntime,IHybridCpuOriginalVirtualIoSnapshotReleaseRuntimeV1,IHybridCpuOriginalParentDeviceCloseContinuationRuntimeV1,IHybridCpuOriginalVirtualIoCloseContinuationRuntimeV1 { }
    public class Spy : ExactNestedArtifactConsumerTests.ArtifactSpy
    {
        public bool LoseRelease;
        public bool LoseIoClose;
        public bool LoseDeviceClose;
        public bool LoseSnapshot;
        public bool SnapshotBeforeEffect;
        public ExternalOriginalVirtualIoSnapshotReleaseRequest? SnapshotRequest;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(LoseRelease && method!.Name=="ResolveChildExecutableArtifactRelease")throw new IOException("Original Release query lost.");
            if(LoseIoClose && method!.Name=="ResolveOriginalVirtualIoClosure")throw new IOException("Original IO Close query lost.");
            if(LoseDeviceClose && method!.Name=="ResolveOriginalParentDeviceClosure")throw new IOException("Original device Close query lost.");
            if(LoseSnapshot && method!.Name=="ResolveOriginalVirtualIoSnapshotRelease")throw new IOException("Original snapshot Release query lost.");
            if(method!.Name=="ReleaseOriginalVirtualIoSnapshot")
            {
                SnapshotRequest=(ExternalOriginalVirtualIoSnapshotReleaseRequest)args![0]!;
                if(SnapshotBeforeEffect)throw new IOException("Snapshot Release delivery lost before native admission.");
            }
            object? raw =base.Invoke(method,args);
            if(LoseRelease && method!.Name=="ReleaseChildExecutableArtifact")throw new IOException("Original Release reply lost after backing join.");
            if(LoseIoClose && method!.Name=="CloseOriginalVirtualIo")throw new IOException("Original IO Close reply lost after effect.");
            if(LoseDeviceClose && method!.Name=="CloseOriginalParentDevice")throw new IOException("Original device Close reply lost after clearing backing.");
            if(LoseSnapshot && method!.Name=="ReleaseOriginalVirtualIoSnapshot")throw new IOException("Original snapshot Release reply lost after effect.");
            return raw;
        }
    }
    [Theory]
    [InlineData(false,false,false,false)] [InlineData(true,false,false,false)] [InlineData(false,true,false,false)] [InlineData(true,true,false,false)]
    [InlineData(false,false,true,false)] [InlineData(true,false,true,false)]
    [InlineData(false,false,false,true)] [InlineData(true,false,false,true)]
    public async Task UnknownArtifactReleaseDeniesNewBytesButPreservesIndependentIoClosure(bool descendant,bool duringAdmission,bool closeUnknown,bool snapshot)
    {
        IRuntime native =DispatchProxy.Create<IRuntime,Spy>(); Spy spy =(Spy)(object)native;
        ExactArtifactReleaseConsumerTests.IOriginalRootRuntime rootRuntime =DispatchProxy.Create<ExactArtifactReleaseConsumerTests.IOriginalRootRuntime,ExactArtifactReleaseConsumerTests.RootSpy>();
        ExactArtifactReleaseConsumerTests.RootSpy rootSpy =(ExactArtifactReleaseConsumerTests.RootSpy)(object)rootRuntime;rootSpy.Inner=spy.Inner;
        HybridCpuExecutableChildAdapter adapter =new HybridCpuExecutableChildAdapter(rootRuntime,native);
        HybridCpuPlatformAuthorityProvider provider =new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(),adapter);
        RuntimeKernel kernel =new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel,991,1991); ProcessHandle service =TestFixtures.Create(kernel,992,1992).Handle;
        ProcessHandle admin =TestFixtures.Create(kernel,99,999).Handle;
        ProcessHandle foreignOwner =snapshot?TestFixtures.Create(kernel,993,1993).Handle:service;
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(foreignOwner,16).Value!;foreign.Span[0]=0x7E;
        CapabilityId budgetCap =kernel.MintCapability(new(999),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        ulong initialOwned =(closeUnknown?ArtifactCharge + 1024UL:ArtifactCharge + 512UL)+(snapshot?2UL:0UL); int initialTransfers =(closeUnknown?2:1)+(snapshot?1:0);
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(admin,budgetCap,owner,"artifact-io",[new(ServiceBudgetDimension.OwnedMemoryBytes,initialOwned),new(ServiceBudgetDimension.PinnedMappedMemoryBytes,CodeCharge),new(ServiceBudgetDimension.GuestMemoryBytes,CodeCharge),new(ServiceBudgetDimension.RegionUses,8)]).Value!.ProcessBudget;
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        OwnedBuffer<byte> region =kernel.AllocateBuffer<byte>(owner,(int)CodeCharge).Value!;
        CapabilityId create =kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet root =kernel.CreateVirtualDomain(owner,create,new(1,32768)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,root.Domain,root.ConfigureCapability).IsSuccess);
        VirtualDomainAuthoritySet child =kernel.CreateNestedVirtualDomain(owner,root.Domain,root.ConfigureCapability,new(new(1,16384),VirtualDomainAuthorityClass.Lifecycle|VirtualDomainAuthorityClass.Execution|VirtualDomainAuthorityClass.GuestMemory|VirtualDomainAuthorityClass.Io)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,child.Domain,child.ConfigureCapability).IsSuccess);
        CapabilityId memory =kernel.MintCapability(process.DomainId,owner,ResourceKind.MemoryRegion,CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),CapabilityRights.Map|CapabilityRights.Read|CapabilityRights.Execute).Value!.CapabilityId;
        KernelResult<GuestRegionMapping> mapped =kernel.MapGuestRegion(owner,child.Domain,child.MemoryCapability,memory,region.Handle,new(0,(long)CodeCharge),GuestMemoryAccess.Read|GuestMemoryAccess.Execute,16384);
        Assert.True(mapped.IsSuccess,mapped.Message);
        VirtualDomainAuthoritySet target =child;
        if(descendant)
        {
            target=kernel.CreateNestedVirtualDomain(owner,child.Domain,child.ConfigureCapability,new(new(1,1024),VirtualDomainAuthorityClass.Lifecycle|VirtualDomainAuthorityClass.Io)).Value!;
            Assert.True(kernel.ConfigureVirtualDomain(owner,target.Domain,target.ConfigureCapability).IsSuccess);
        }
        PlatformDomainBinding binding =kernel.QueryVirtualDomainPlatformBinding(owner,target.Domain).Value!;
        CapabilityId deviceCap =kernel.MintCapability(process.DomainId,owner,ResourceKind.Device,"device/artifact-io",CapabilityRights.Configure|CapabilityRights.Read|CapabilityRights.Write).Value!.CapabilityId;
        PlatformDeviceLease device =kernel.BindPlatformDevice(owner,binding,deviceCap,PlatformDeviceRights.Read|PlatformDeviceRights.Write).Value!;
        VirtualIoBinding io =kernel.BindVirtualIo(owner,target.Domain,device,new(PlatformDeviceRights.Read|PlatformDeviceRights.Write,512)).Value!;
        VirtualIoBinding? peer=null;
        if(closeUnknown)
        {
            CapabilityId peerCap =kernel.MintCapability(process.DomainId,owner,ResourceKind.Device,"device/artifact-io-peer",CapabilityRights.Configure|CapabilityRights.Read|CapabilityRights.Write).Value!.CapabilityId;
            PlatformDeviceLease peerDevice =kernel.BindPlatformDevice(owner,binding,peerCap,PlatformDeviceRights.Read|PlatformDeviceRights.Write).Value!;
            KernelResult<VirtualIoBinding> peerResult =kernel.BindVirtualIo(owner,target.Domain,peerDevice,new(PlatformDeviceRights.Read|PlatformDeviceRights.Write,512));
            Assert.True(peerResult.IsSuccess,peerResult.Message);peer=peerResult.Value;
        }
        KernelResult artifact =kernel.BindVirtualDomainExecutable(owner,child.Domain,child.ExecuteCapability,mapped.Value!.Mapping,ReadPackage(),512);
        Assert.True(artifact.IsSuccess,artifact.Message);
        Assert.True(kernel.TransferVirtualIo(owner,new(io,0,true,1,[0x51])).IsSuccess);
        if(peer is { } livePeer)Assert.True(kernel.TransferVirtualIo(owner,new(livePeer,0,true,1,[0x52])).IsSuccess);
        IDictionary nativeRoots =(IDictionary)spy.Inner.GetType().GetField("registry",Private)!.GetValue(spy.Inner)!;
        object nativeRoot =Assert.Single(nativeRoots.Values.Cast<object>());
        IList devices =(IList)nativeRoot.GetType().GetProperty("OriginalParentDevices")!.GetValue(nativeRoot)!;
        object backingOwner =devices.Cast<object>().First();
        byte[] backing =(byte[])backingOwner.GetType().GetField("Backing")!.GetValue(backingOwner)!;
        byte[]? peerBacking=null;
        if(closeUnknown)
        {
            object peerOwner =devices.Cast<object>().Skip(1).Single();
            peerBacking=(byte[])peerOwner.GetType().GetField("Backing")!.GetValue(peerOwner)!;
            Assert.Equal(0x52,peerBacking[0]);
        }
        Assert.Equal(0x51,backing[0]);
        Assert.Equal(initialOwned-(snapshot?2UL:0UL),Used());
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"artifact-io",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        RuntimeVirtualizationServiceHost? snapshotHost=null;Task<VirtualIoTransferResponse>? snapshotRead=null;
        if(snapshot)
        {
            EndpointSessionHandle readSession =kernel.OpenSession(owner,descriptor,[]).Value;
            snapshotHost=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,readSession).Value!;
            IVirtualizationService readClient =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,readSession));
            spy.LoseSnapshot=true;
            spy.SnapshotBeforeEffect=descendant;
            snapshotRead=readClient.TransferIoAsync(new(io.Id,io.Generation,0,false,2,default)).AsTask();
            Assert.False(snapshotHost.ProcessNext().IsSuccess);Assert.False(snapshotRead.IsCompleted);
            Assert.Equal(spy.SnapshotBeforeEffect?0:1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));Assert.Equal(initialOwned,Used());
            if(spy.SnapshotBeforeEffect)Assert.Equal(new byte[]{0x51,0},spy.SnapshotRequest!.Transfer.CopyReadData());
        }
        PlatformAuthorityBridge bridge =kernel.PlatformAuthority;
        IDictionary guests =(IDictionary)bridge.GetType().GetField("_originalGuestOwners",Private)!.GetValue(bridge)!;
        object guest =Assert.Single(guests.Values.Cast<object>());
        PlatformGuestMapping mapping =(PlatformGuestMapping)guest.GetType().GetProperty("Mapping")!.GetValue(guest)!;
        MethodInfo release =bridge.GetType().GetMethod("ReleaseArtifactsForGuestMapping",Private)!;
        spy.LoseRelease=true;
        int observed =0;
        void ReleaseOriginal()
        {
            Assert.False(((KernelResult)release.Invoke(bridge,[mapping])!).IsSuccess);
            Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseChildExecutableArtifact"));
        }
        if(!duringAdmission)ReleaseOriginal();
        else rootSpy.BeforeQuery=()=>
        {
            IDictionary bindings =(IDictionary)bridge.GetType().GetField("_virtualIoBindings",Private)!.GetValue(bridge)!;
            object originalIo =Assert.Single(bindings.Values.Cast<object>());
            if(((IEnumerable)originalIo.GetType().GetProperty("Transfers")!.GetValue(originalIo)!).Cast<object>().Count()<2)return;
            rootSpy.BeforeQuery=null;observed++;
            Assert.False(Monitor.IsEntered(bridge.GetType().GetField("_secureDomainLifecycleGate",Private)!.GetValue(bridge)!));
            Assert.False(Monitor.IsEntered(provider.GetType().GetField("_childCreateCommitGate",Private)!.GetValue(provider)!));
            Assert.False(Monitor.IsEntered(adapter.GetType().GetField("sync",Private)!.GetValue(adapter)!));
            ReleaseOriginal();
            Assert.False(kernel.TransferVirtualIo(owner,new(io,0,true,1,[0xEF])).IsSuccess);
        };
        EndpointSessionHandle session =kernel.OpenSession(owner,descriptor,[]).Value;
        RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        IVirtualizationService client =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        Task<VirtualIoTransferResponse> pending =client.TransferIoAsync(new(io.Id,io.Generation,0,true,1,new global::SingPlus.Sip.Native.BoundedBytes(new byte[]{0xEE}))).AsTask();
        KernelResult published =host.ProcessNext();
        Assert.Equal((initialTransfers,(byte)0x51),(spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"),backing[0]));
        Assert.False(published.IsSuccess);
        await Assert.ThrowsAnyAsync<Exception>(async()=>await pending);
        Assert.Equal(duringAdmission?1:0,observed);
        Assert.Equal(initialTransfers,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
        object providerIo =((IDictionary)provider.GetType().GetField("_originalProviderVirtualIo",Private)!.GetValue(provider)!).Values.Cast<object>().First();
        PlatformProviderVirtualIoLease providerLease =(PlatformProviderVirtualIoLease)providerIo.GetType().GetProperty("Lease")!.GetValue(providerIo)!;
        Assert.False(provider.TransferOriginalVirtualIo(new(providerLease,0,true,1,[0xEE])).IsSuccess);
        object adapterIo =((IDictionary)adapter.GetType().GetField("originalIo",Private)!.GetValue(adapter)!).Values.Cast<object>().First();
        NeutralVirtualIoLease adapterLease =(NeutralVirtualIoLease)adapterIo.GetType().GetField("Lease")!.GetValue(adapterIo)!;
        Assert.False(adapter.TransferOriginalVirtualIo(new(adapterLease,0,true,1,[0xEE])).IsSuccess);
        IDictionary nativeChildren =(IDictionary)spy.Inner.GetType().GetField("childRegistry",Private)!.GetValue(spy.Inner)!;
        object nativeArtifactOwner =nativeChildren.Values.Cast<object>().Single(value=>value.GetType().GetProperty("ArtifactRelease")!.GetValue(value) is not null);
        object nativeRelease =nativeArtifactOwner.GetType().GetProperty("ArtifactRelease")!.GetValue(nativeArtifactOwner)!;
        PropertyInfo releaseObservation =nativeRelease.GetType().GetProperty("Observation")!;
        ExternalArtifactReleaseReceipt originalRelease =(ExternalArtifactReleaseReceipt)releaseObservation.GetValue(nativeRelease)!;
        ExternalOriginalVirtualIoRequest nativeAdmission =(ExternalOriginalVirtualIoRequest)adapterIo.GetType().GetField("NativeRequest")!.GetValue(adapterIo)!;
        object nativeIo =Assert.Single(((IList)backingOwner.GetType().GetProperty("Users")!.GetValue(backingOwner)!).Cast<object>());
        FieldInfo issued =nativeIo.GetType().GetField("TransfersIssued")!; ulong beforeNative =(ulong)issued.GetValue(nativeIo)!;
        foreach(object? observation in new object?[]{null,originalRelease with { }})
        {
            releaseObservation.SetValue(nativeRelease,observation);
            ExternalOriginalVirtualIoTransferRequest refused =new(nativeAdmission,new(new(Guid.NewGuid()),new(778)),0,true,1,[0xEE]);
            ExternalOriginalVirtualIoTransferReceipt receipt =Assert.IsType<ExternalOriginalVirtualIoTransferReceipt>(spy.Inner.TransferOriginalVirtualIo(refused));
            Assert.True(receipt.NoEffect);Assert.False(receipt.Completed);
            Assert.Same(receipt,spy.Inner.ResolveOriginalVirtualIoTransfer(refused));
            Assert.Same(receipt,spy.Inner.TransferOriginalVirtualIo(refused));
            beforeNative++;
            Assert.False(spy.Inner.TransferOriginalVirtualIo(nativeAdmission,0,[0xEE],true));
            Assert.Equal(beforeNative,issued.GetValue(nativeIo));Assert.Equal(0x51,backing[0]);Assert.Equal(initialOwned,Used());
        }
        releaseObservation.SetValue(nativeRelease,originalRelease);
        Assert.Equal((initialTransfers,(byte)0x51),(spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"),backing[0]));
        Assert.Equal(initialOwned,Used());Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        Assert.Equal(0x7E,foreign.Span[0]);
        if(snapshot)
        {
            Assert.True(kernel.CloseVirtualIo(io).IsSuccess);
            Assert.Throws<InvalidOperationException>(()=>spy.SnapshotRequest!.Transfer.CopyReadData());
            Assert.Equal(ArtifactCharge,Used());Assert.All(backing,value=>Assert.Equal(0,value));
            KernelResult serviceExit =kernel.TerminateProcess(service);
            Assert.True(serviceExit.IsSuccess,serviceExit.Message);
            Assert.Equal(ArtifactCharge,Used());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await snapshotRead!);
            Assert.Equal(initialTransfers,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
            Assert.Equal(spy.SnapshotBeforeEffect?0:1,spy.Calls.GetValueOrDefault("ReleaseOriginalVirtualIoSnapshot"));
            Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseChildExecutableArtifact"));
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            Assert.Equal(0x7E,foreign.Span[0]);
        }
        if(closeUnknown)
        {
            spy.LoseIoClose=true;
            Assert.False(kernel.CloseVirtualIo(io).IsSuccess);
            Assert.False(kernel.CloseVirtualIo(io).IsSuccess);
            Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
            Assert.Equal(0,spy.Calls.GetValueOrDefault("CloseOriginalParentDevice"));
            Assert.Equal(ArtifactCharge + 1024UL,Used());Assert.Equal(0x51,backing[0]);Assert.Equal(0x52,peerBacking![0]);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            Assert.Equal(0x7E,foreign.Span[0]);
            spy.LoseIoClose=false;
            spy.LoseDeviceClose=true;
            Assert.False(kernel.CloseVirtualIo(io).IsSuccess);
            Assert.False(kernel.CloseVirtualIo(io).IsSuccess);
            Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
            Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalParentDevice"));
            Assert.All(backing,value=>Assert.Equal(0,value));Assert.Equal(ArtifactCharge + 1024UL,Used());Assert.Equal(0x52,peerBacking[0]);
            Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseChildExecutableArtifact"));
            spy.LoseDeviceClose=false;
            Assert.True(kernel.CloseVirtualIo(io).IsSuccess);
            Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
            Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalParentDevice"));
            Assert.All(backing,value=>Assert.Equal(0,value));Assert.Equal(ArtifactCharge + 512UL,Used());
            Assert.Equal(0x52,peerBacking![0]);
            Assert.False(kernel.TransferVirtualIo(owner,new(peer!.Value,0,true,1,[0xEF])).IsSuccess);
            Assert.Equal(initialTransfers,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));Assert.Equal(0x52,peerBacking[0]);
            Assert.False(kernel.DestroyVirtualDomain(owner,child.Domain,child.ConfigureCapability).IsSuccess);
            if(descendant)
            {
                Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
                Assert.Equal(ArtifactCharge + 512UL,Used());Assert.Equal(0x52,peerBacking[0]);
                Assert.True(kernel.CloseVirtualIo(peer.Value).IsSuccess);
            }
            Assert.Equal(2,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));Assert.All(peerBacking,value=>Assert.Equal(0,value));
            Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseChildExecutableArtifact"));
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            Assert.Equal(ArtifactCharge,Used());Assert.Equal(0x7E,foreign.Span[0]);
        }
        spy.LoseRelease=false;
        Assert.True(((KernelResult)release.Invoke(bridge,[mapping])!).IsSuccess);
        Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseChildExecutableArtifact"));Assert.Equal(closeUnknown||snapshot?ArtifactCharge:ArtifactCharge + 512UL,Used());
        if(!closeUnknown && !snapshot)
        {
            Assert.True(kernel.TransferVirtualIo(owner,new(io,0,true,1,[0x61])).IsSuccess);
            Assert.Equal(0x61,backing[0]);
            Assert.Equal(2,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
            Assert.True(kernel.CloseVirtualIo(io).IsSuccess);Assert.Equal(ArtifactCharge,Used());
        }
        else
        {
            Assert.False(kernel.TransferVirtualIo(owner,new(io,0,true,1,[0x61])).IsSuccess);
            Assert.Equal(initialTransfers,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
        }
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage,u=>Assert.Equal(0UL,u.Used));
        Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseChildExecutableArtifact"));Assert.Equal(closeUnknown?2:1,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
        Assert.Equal(0x7E,foreign.Span[0]);
    }
    [Fact]
    public void ActualNativeBackingRetirementOverlapKeepsNewBytesDeniedAndIndependentIoCloseAvailable()
    {
        IRuntime native =DispatchProxy.Create<IRuntime,Spy>(); Spy spy =(Spy)(object)native;
        HybridCpuExecutableChildAdapter adapter =new HybridCpuExecutableChildAdapter(spy.Inner,native);
        HybridCpuPlatformAuthorityProvider provider =new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(),adapter);
        RuntimeKernel kernel =new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel,994,1994); ProcessHandle service =TestFixtures.Create(kernel,995,1995).Handle;
        ProcessHandle admin =TestFixtures.Create(kernel,99,999).Handle;
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(service,16).Value!;foreign.Span[0]=0x7E;
        CapabilityId cap =kernel.MintCapability(new(999),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(admin,cap,owner,"retirement-overlap",[new(ServiceBudgetDimension.OwnedMemoryBytes,ArtifactCharge + 512UL),new(ServiceBudgetDimension.PinnedMappedMemoryBytes,CodeCharge),new(ServiceBudgetDimension.GuestMemoryBytes,CodeCharge),new(ServiceBudgetDimension.RegionUses,8)]).Value!.ProcessBudget;
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        OwnedBuffer<byte> region =kernel.AllocateBuffer<byte>(owner,(int)CodeCharge).Value!;
        CapabilityId create =kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet root =kernel.CreateVirtualDomain(owner,create,new(1,32768)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,root.Domain,root.ConfigureCapability).IsSuccess);
        VirtualDomainAuthoritySet child =kernel.CreateNestedVirtualDomain(owner,root.Domain,root.ConfigureCapability,new(new(1,16384),VirtualDomainAuthorityClass.Lifecycle|VirtualDomainAuthorityClass.Execution|VirtualDomainAuthorityClass.GuestMemory|VirtualDomainAuthorityClass.Io)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,child.Domain,child.ConfigureCapability).IsSuccess);
        CapabilityId memoryCap =kernel.MintCapability(process.DomainId,owner,ResourceKind.MemoryRegion,CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),CapabilityRights.Map|CapabilityRights.Read|CapabilityRights.Execute).Value!.CapabilityId;
        GuestRegionMapping mapped =kernel.MapGuestRegion(owner,child.Domain,child.MemoryCapability,memoryCap,region.Handle,new(0,(long)CodeCharge),GuestMemoryAccess.Read|GuestMemoryAccess.Execute,16384).Value!;
        VirtualDomainAuthoritySet target =kernel.CreateNestedVirtualDomain(owner,child.Domain,child.ConfigureCapability,new(new(1,1024),VirtualDomainAuthorityClass.Lifecycle|VirtualDomainAuthorityClass.Io)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,target.Domain,target.ConfigureCapability).IsSuccess);
        PlatformDomainBinding binding =kernel.QueryVirtualDomainPlatformBinding(owner,target.Domain).Value!;
        CapabilityId deviceCap =kernel.MintCapability(process.DomainId,owner,ResourceKind.Device,"device/retirement-overlap",CapabilityRights.Configure|CapabilityRights.Read|CapabilityRights.Write).Value!.CapabilityId;
        PlatformDeviceLease device =kernel.BindPlatformDevice(owner,binding,deviceCap,PlatformDeviceRights.Read|PlatformDeviceRights.Write).Value!;
        VirtualIoBinding io =kernel.BindVirtualIo(owner,target.Domain,device,new(PlatformDeviceRights.Read|PlatformDeviceRights.Write,512)).Value!;
        Assert.True(kernel.BindVirtualDomainExecutable(owner,child.Domain,child.ExecuteCapability,mapped.Mapping,ReadPackage(),512).IsSuccess);
        Assert.True(kernel.TransferVirtualIo(owner,new(io,0,true,1,[0x51])).IsSuccess);
        PlatformAuthorityBridge bridge =kernel.PlatformAuthority;
        object guest =Assert.Single(((IDictionary)bridge.GetType().GetField("_originalGuestOwners",Private)!.GetValue(bridge)!).Values.Cast<object>());
        PlatformGuestMapping guestMapping =(PlatformGuestMapping)guest.GetType().GetProperty("Mapping")!.GetValue(guest)!;
        object nativeOwner =((IDictionary)spy.Inner.GetType().GetField("childRegistry",Private)!.GetValue(spy.Inner)!).Values.Cast<object>().Single(value=>value.GetType().GetProperty("Artifact")!.GetValue(value) is not null);
        object execution =nativeOwner.GetType().GetProperty("Execution")!.GetValue(nativeOwner)!;
        object memory =execution.GetType().GetField("memory",Private)!.GetValue(execution)!;
        object memoryGate =memory.GetType().GetField("sync",Private)!.GetValue(memory)!;
        PropertyInfo releaseProperty =nativeOwner.GetType().GetProperty("ArtifactRelease")!;
        object adapterIo =Assert.Single(((IDictionary)adapter.GetType().GetField("originalIo",Private)!.GetValue(adapter)!).Values.Cast<object>());
        ExternalOriginalVirtualIoRequest admission =(ExternalOriginalVirtualIoRequest)adapterIo.GetType().GetField("NativeRequest")!.GetValue(adapterIo)!;
        IEnumerable transfers =(IEnumerable)adapterIo.GetType().GetProperty("Transfers")!.GetValue(adapterIo)!;
        ExternalOriginalVirtualIoTransferRequest originalTransfer =(ExternalOriginalVirtualIoTransferRequest)Assert.Single(transfers.Cast<object>()).GetType().GetField("Native")!.GetValue(Assert.Single(transfers.Cast<object>()))!;
        ExternalOriginalVirtualIoTransferReceipt? originalRaw =spy.Inner.ResolveOriginalVirtualIoTransfer(originalTransfer);Assert.NotNull(originalRaw);
        object nativeRoot =nativeOwner.GetType().GetProperty("ParentSource")!.GetValue(nativeOwner)!;
        IList nativeDevices =(IList)nativeRoot.GetType().GetProperty("OriginalParentDevices")!.GetValue(nativeRoot)!;
        object nativeDevice =Assert.Single(nativeDevices.Cast<object>());
        byte[] ioBacking =(byte[])nativeDevice.GetType().GetField("Backing")!.GetValue(nativeDevice)!;
        object nativeIo =Assert.Single(((IList)nativeDevice.GetType().GetProperty("Users")!.GetValue(nativeDevice)!).Cast<object>());
        FieldInfo issued =nativeIo.GetType().GetField("TransfersIssued")!;Assert.Equal(1UL,issued.GetValue(nativeIo));Assert.Equal(0x51,ioBacking[0]);
        MethodInfo release =bridge.GetType().GetMethod("ReleaseArtifactsForGuestMapping",Private)!;
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"retirement-overlap",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(owner,descriptor,[]).Value; RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        IVirtualizationService client =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        Task<KernelResult>? pendingRelease=null;spy.LoseRelease=true;
        Monitor.Enter(memoryGate);
        try
        {
            pendingRelease=Task.Run(()=>(KernelResult)release.Invoke(bridge,[guestMapping])!);
            Assert.True(SpinWait.SpinUntil(()=>releaseProperty.GetValue(nativeOwner) is not null && !(bool)execution.GetType().GetField("materialized",Private)!.GetValue(execution)!,TimeSpan.FromSeconds(5)));
            Assert.False(pendingRelease.IsCompleted);Assert.False((bool)memory.GetType().GetProperty("Retired",Private)!.GetValue(memory)!);
            object fact =releaseProperty.GetValue(nativeOwner)!; ExternalArtifactReleaseRequest request =(ExternalArtifactReleaseRequest)fact.GetType().GetProperty("Request")!.GetValue(fact)!;
            Assert.Null(fact.GetType().GetProperty("Observation")!.GetValue(fact));Assert.Null(spy.Inner.ResolveChildExecutableArtifactRelease(request));
            Assert.Null(spy.Inner.ReleaseChildExecutableArtifact(request).Receipt);
            Assert.Equal(1UL,nativeOwner.GetType().GetProperty("ArtifactReleasesIssued")!.GetValue(nativeOwner));
            ExternalOriginalVirtualIoTransferRequest refused =new(admission,new(new(Guid.NewGuid()),new(779)),0,true,1,[0xEE]);
            ExternalOriginalVirtualIoTransferReceipt receipt =Assert.IsType<ExternalOriginalVirtualIoTransferReceipt>(spy.Inner.TransferOriginalVirtualIo(refused));
            Assert.True(receipt.NoEffect);Assert.False(receipt.Completed);
            Assert.Same(receipt,spy.Inner.ResolveOriginalVirtualIoTransfer(refused));
            Assert.Same(receipt,spy.Inner.TransferOriginalVirtualIo(refused));
            Assert.False(spy.Inner.TransferOriginalVirtualIo(admission,0,[0xEE],true));
            Assert.Equal(2UL,issued.GetValue(nativeIo));Assert.Equal(0x51,ioBacking[0]);
            Assert.Same(originalRaw,spy.Inner.ResolveOriginalVirtualIoTransfer(originalTransfer));
            Task<VirtualIoTransferResponse> write =client.TransferIoAsync(new(io.Id,io.Generation,0,true,1,new global::SingPlus.Sip.Native.BoundedBytes(new byte[]{0xEE}))).AsTask();
            Assert.False(host.ProcessNext().IsSuccess);Assert.True(SpinWait.SpinUntil(()=>write.IsCompleted,TimeSpan.FromSeconds(5)));
            Assert.ThrowsAny<Exception>(()=>write.GetAwaiter().GetResult());
            Assert.Equal(1,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));Assert.Equal(ArtifactCharge + 512UL,Used());
            Assert.Equal(2UL,issued.GetValue(nativeIo));Assert.Equal(0x51,ioBacking[0]);
            Assert.True(kernel.CloseVirtualIo(io).IsSuccess);Assert.Equal(ArtifactCharge,Used());Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
            Assert.All(ioBacking,value=>Assert.Equal(0,value));
            Assert.False(pendingRelease.IsCompleted);Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);Assert.Equal(0x7E,foreign.Span[0]);
        }
        finally
        {
            Monitor.Exit(memoryGate);
            if(pendingRelease is not null)Assert.True(pendingRelease.Wait(TimeSpan.FromSeconds(15)));
        }
        Assert.False(pendingRelease!.Result.IsSuccess);Assert.True((bool)memory.GetType().GetProperty("Retired",Private)!.GetValue(memory)!);
        Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseChildExecutableArtifact"));Assert.Equal(ArtifactCharge,Used());
        spy.LoseRelease=false;Assert.True(((KernelResult)release.Invoke(bridge,[guestMapping])!).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.All(kernel.QueryBudget(budget).Value!.Usage,u=>Assert.Equal(0UL,u.Used));
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseChildExecutableArtifact"));Assert.Equal(0x7E,foreign.Span[0]);
    }
    private static byte[] ReadPackage()
    {
        for(DirectoryInfo? directory =new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)
        {
            string path =Path.Combine(directory.FullName,"tests","SingPlus.Tests","Fixtures","OriginalNestedStart","return42.hcexe");
            if(File.Exists(path))return File.ReadAllBytes(path);
        }
        throw new FileNotFoundException("Compiler-produced package missing.");
    }
}

