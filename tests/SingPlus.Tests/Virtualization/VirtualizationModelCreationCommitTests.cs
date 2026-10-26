using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Virtualization;
using Xunit;

namespace SingPlus.Tests.Virtualization;

public sealed class VirtualizationModelCreationCommitTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedModelCreationServiceExitClosesKnownLeaseAndRetainsUnknown(bool lostLease)
    {
        ObservedModelProvider provider =new ObservedModelProvider(); RuntimeKernel kernel =new RuntimeKernel(provider);
        ProcessHandle caller =TestFixtures.Create(kernel,3001,30010).Handle; ProcessHandle service =TestFixtures.Create(kernel,3002,30020).Handle;
        ProcessHandle foreignOwner =TestFixtures.Create(kernel,3003,30030).Handle;
        OwnedBuffer<byte> region =kernel.AllocateBuffer<byte>(caller,64).Value!;region.Span[0]=0x48;
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(foreignOwner,16).Value!;foreign.Span[0]=0x71;
        CapabilityId cap =kernel.MintCapability(new(30010),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        ProtocolDefinitionV1 protocol =SingPlus.Sip.Virtualization.VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"failed-model-exit",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,SingPlus.Sip.Virtualization.VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(caller,descriptor,[]).Value;
        RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        provider.AfterCreate=()=>{if(lostLease)throw new IOException("Model lease reply lost after effect");Assert.True(kernel.RevokeCapability(cap).IsSuccess);};
        ValueTask<VirtualDomainAuthorityResponse> reply =SingPlus.Sip.Virtualization.VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session)).CreateAsync(new(cap,new(1,4096)));
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await reply);
        provider.AfterCreate=null;
        KernelResult exit =kernel.TerminateProcess(service);
        Assert.Equal(!lostLease,exit.IsSuccess);
        Assert.Equal(1,provider.CreateCalls);Assert.Equal(lostLease?0:1,provider.RevokeCalls);
        Assert.Equal(0x48,region.Span[0]);Assert.Equal(0x71,foreign.Span[0]);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        Assert.Equal(!lostLease,kernel.TerminateProcess(caller).IsSuccess);
        Assert.Equal(lostLease?RegionState.Owned:RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        Assert.Equal(1,provider.CreateCalls);Assert.Equal(lostLease?0:1,provider.RevokeCalls);
        Assert.Equal(0x71,foreign.Span[0]);Assert.True(kernel.TerminateProcess(foreignOwner).IsSuccess);
    }

    [Theory]
    [InlineData(0,false)] [InlineData(1,false)] [InlineData(2,false)]
    [InlineData(0,true)] [InlineData(1,true)] [InlineData(2,true)]
    [InlineData(3,false)] [InlineData(3,true)]
    public async Task AcceptedModelCreateRetainsIntentBeforeSourceAndClosesNeverInvoked(int fault,bool overlap)
    {
        ObservedModelProvider provider =new ObservedModelProvider(); RuntimeKernel kernel =new RuntimeKernel(provider);
        ProcessHandle caller =TestFixtures.Create(kernel,2941,29410).Handle; ProcessHandle service =TestFixtures.Create(kernel,2942,29420).Handle;
        OwnedBuffer<byte> region =kernel.AllocateBuffer<byte>(caller,64).Value!; OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(service,32).Value!;
        CapabilityId cap =kernel.MintCapability(new(29410),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        ProtocolDefinitionV1 protocol =SingPlus.Sip.Virtualization.VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"pre-effect-source",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,SingPlus.Sip.Virtualization.VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(caller,descriptor,[]).Value;
        RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        IVirtualizationService client =SingPlus.Sip.Virtualization.VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session));
        object? retainedInvocation=null;
        provider.BeforeSource=()=>
        {
            provider.BeforeSource=null;
            BindingFlags flags =global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic;
            IDictionary pending =(global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_pendingVirtualDomainCreates",flags)!.GetValue(kernel.PlatformAuthority)!;
            object original =Assert.Single(pending.Values.Cast<object>());
            Assert.False((bool)original.GetType().GetProperty("EffectMayHaveOccurred")!.GetValue(original)!);
            EndpointSessionInvocationRegistry registry =(EndpointSessionInvocationRegistry)typeof(RuntimeKernel).GetField("_endpointSessionInvocationRegistry",flags)!.GetValue(kernel)!;
            IDictionary records =(global::System.Collections.IDictionary)registry.GetType().GetField("_ownedRecords",flags)!.GetValue(registry)!;
            object record =Assert.Single(records.Values.Cast<object>());
            retainedInvocation=record;
            Assert.Same(original,record.GetType().GetProperty("OriginalVirtualCreate")!.GetValue(record));
            EndpointSessionInvocationHandle invocation =(EndpointSessionInvocationHandle)record.GetType().GetProperty("Handle")!.GetValue(record)!;
            Assert.False(registry.ConsumeVirtualCreationNeverInvoked(invocation,service,original).IsSuccess);
            Assert.False(registry.ConsumeVirtualCreationNeverInvoked(invocation,caller,new object()).IsSuccess);
            if(overlap)Assert.False(kernel.TerminateProcess(caller).IsSuccess);
            if(fault==0)throw new IOException("Pre-effect source unavailable");
            if(fault==1)Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
            if(fault==2)Assert.True(kernel.RevokeCapability(cap).IsSuccess);
            if(fault==3)pending.Clear(); // Source callback loses routing before effect admission.
        };
        ValueTask<VirtualDomainAuthorityResponse> request =client.CreateAsync(new(cap,new(1,4096)));
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await request);
        Assert.NotNull(retainedInvocation);
        Assert.True((bool)retainedInvocation.GetType().GetProperty("VirtualCreateNeverInvoked")!.GetValue(retainedInvocation)!);
        KernelResult closed =kernel.TerminateProcess(caller);Assert.True(closed.IsSuccess,$"{closed.Message}; teardown={kernel.QueryProcessTeardown(caller).Value}; terminal={retainedInvocation.GetType().GetProperty("TerminalStatus")!.GetValue(retainedInvocation)}");
        Assert.Equal(0,provider.CreateCalls);Assert.Equal(0,provider.RevokeCalls);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==foreign.Handle).State);
        Assert.True(kernel.Processes.Resolve(service).IsSuccess);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AcceptedModelRefusalWithChangedSourceRetainsCallerWithoutRepeat(bool unavailable)
    {
        ObservedModelProvider provider =new ObservedModelProvider();
        typeof(HostPlatformAuthorityProvider).GetField("_nextVirtualDomainId",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.SetValue(provider._inner,ulong.MaxValue);
        provider.AfterCreate=()=>{if(unavailable)provider.ThrowSource=true;else provider.Incarnation=new(2);};
        RuntimeKernel kernel =new RuntimeKernel(provider);
        ProcessHandle caller =TestFixtures.Create(kernel,2931,29310).Handle; ProcessHandle service =TestFixtures.Create(kernel,2932,29320).Handle;
        OwnedBuffer<byte> region =kernel.AllocateBuffer<byte>(caller,64).Value!;
        CapabilityId cap =kernel.MintCapability(new(29310),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        ProtocolDefinitionV1 protocol =SingPlus.Sip.Virtualization.VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"source-refusal",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,SingPlus.Sip.Virtualization.VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(caller,descriptor,[]).Value;
        RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        IVirtualizationService client =SingPlus.Sip.Virtualization.VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session));
        ValueTask<VirtualDomainAuthorityResponse> pending =client.CreateAsync(new(cap,new(1,4096)));
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await pending);
        Assert.False(kernel.TerminateProcess(caller).IsSuccess);
        provider.ThrowSource=false;provider.Incarnation=new(1);
        Assert.False(kernel.TerminateProcess(caller).IsSuccess);
        Assert.Equal(1,provider.CreateCalls);Assert.Equal(0,provider.RevokeCalls);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        Assert.True(kernel.Processes.Resolve(service).IsSuccess);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void MutableLocalVirtualSourceCannotPublishAcrossEqualWriteOrAba(int fault)
    {
        ObservedModelProvider provider = new ObservedModelProvider(); RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 992, 9920);
        ProcessHandle admin = TestFixtures.Create(kernel, 993, 9930).Handle;
        CapabilityId budgetSource = kernel.MintCapability(new DomainId(9930), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(admin, budgetSource, owner, "virtual-revision-reclaim",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        CapabilityId source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        OwnedBuffer<byte> region = kernel.AllocateBuffer<byte>(owner, 128).Value!;
        VirtualDomainAuthoritySet created = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        VirtualDomainAuthority.Record local = Assert.Single(Records(kernel).ForOwner(owner));
        VirtualDomainState original = local.State;
        void ChangeSource()
        {
            provider.BeforeSource = null;
            if (fault >= 4)
            {
                local.AuthorityPublished = true; // Equal publication write is also a source revision.
                if (fault >= 6)
                    local.GetType().GetField("_sourceRevisionExhausted", global::System.Reflection.BindingFlags.Instance |
                        global::System.Reflection.BindingFlags.NonPublic)!.SetValue(local, true);
                return;
            }
            if (fault % 2 != 0) local.State = VirtualDomainState.Quarantined;
            local.State = original; // Independent equal write/ABA, not this transaction's state commit.
        }
        bool beforeEffect = fault < 2 || fault is 4 or 6;
        if (beforeEffect) provider.BeforeSource = ChangeSource; else provider.AfterTransition = ChangeSource;
        Assert.False(kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).IsSuccess);
        Assert.Equal(original, local.State);
        Assert.Equal(beforeEffect ? 0 : 1, provider.TransitionCalls);
        Assert.Equal(128UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        provider.BeforeSource = null; provider.AfterTransition = null;
        if (!beforeEffect)
        {
            Assert.False(kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).IsSuccess);
            Assert.Equal(1, provider.TransitionCalls); // Retained known effect cannot be repeated.
        }
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(VirtualDomainState.Closed, local.State);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EqualCopyOfLocalOwnerCannotPublishEvenWhenBothIndexesChange(bool late)
    {
        ObservedModelProvider provider = new ObservedModelProvider(); RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 994, 9940);
        CapabilityId source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        OwnedBuffer<byte> region = kernel.AllocateBuffer<byte>(owner, 128).Value!;
        VirtualDomainAuthoritySet created = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        VirtualDomainAuthority authority = Records(kernel); VirtualDomainAuthority.Record local = Assert.Single(authority.ForOwner(owner));
        IDictionary routes = (global::System.Collections.IDictionary)Field(authority, "_records");
        IDictionary originals = (global::System.Collections.IDictionary)Field(authority, "_originalRecords");
        void Copy()
        {
            provider.BeforeSource = null;
            object copy = typeof(object).GetMethod("MemberwiseClone", global::System.Reflection.BindingFlags.Instance |
                global::System.Reflection.BindingFlags.NonPublic)!.Invoke(local, null)!;
            routes[created.Domain.DomainId] = originals[created.Domain.DomainId] = copy;
        }
        if (late) provider.AfterTransition = Copy; else provider.BeforeSource = Copy;
        Assert.False(kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).IsSuccess);
        Assert.Equal(late ? 1 : 0, provider.TransitionCalls);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        routes[created.Domain.DomainId] = originals[created.Domain.DomainId] = local;
        provider.AfterTransition = null; provider.BeforeSource = null;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
    }

    [Fact]
    public void ModelCapabilityLostInsideSourceCallbackRefusesActualEffect()
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 990, 9900);
        CapabilityId source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        kernel.AllocateRegion(owner, 128);
        provider.BeforeSource = () =>
        {
            provider.BeforeSource = null;
            Assert.True(kernel.RevokeCapability(source).IsSuccess);
        };
        Assert.False(kernel.CreateVirtualDomain(owner, source, new(1, 4096)).IsSuccess);
        Assert.Equal(0, provider.CreateCalls);
        Assert.False(kernel.PlatformAuthority.HasUnresolvedVirtualDomainCreate(new(process.DomainId, owner)));
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)]
    public void ModelOriginalCreateFactRetainsOutcomeAndPinsActualBacking(int outcome)
    {
        ObservedModelProvider provider = new ObservedModelProvider { CreateFailure = outcome == 0 ? PlatformAuthorityStatus.Denied : null, MalformedLease = outcome == 2, MalformedRefusal=outcome==4 };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 991, 9910);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(process.DomainId, owner);
        CapabilityId source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        kernel.AllocateRegion(owner, 128);
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary pending = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_pendingVirtualDomainCreates", flags)!.GetValue(kernel.PlatformAuthority)!;
        IDictionary unresolved = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_unresolvedVirtualDomainCreates", flags)!.GetValue(kernel.PlatformAuthority)!;
        object? original = null;
        provider.AfterCreate = () =>
        {
            original = pending[subject]!;
            Assert.True((bool)original.GetType().GetProperty("EffectMayHaveOccurred")!.GetValue(original)!);
            Assert.False(kernel.CreateVirtualDomain(owner, source, new(1, 4096)).IsSuccess);
            if (outcome == 3) Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
            if (outcome == 1) throw new IOException("Original model reply lost.");
        };
        Assert.False(kernel.CreateVirtualDomain(owner, source, new(1, 4096)).IsSuccess);
        Assert.Same(original, unresolved[subject]);
        Assert.False(pending.Contains(subject));
        object? result = original!.GetType().GetProperty("Observation")!.GetValue(original);
        if (outcome == 1) Assert.Null(result); else Assert.NotNull(result);
        Assert.False(kernel.CreateVirtualDomain(owner, source, new(1, 4096)).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.CreateCalls);
        Assert.Equal(0, provider.RevokeCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }
    [Fact]
    public void ModelResetBeforeCreateEffectDoesNotPinActualReclaim()
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 989, 9890);
        CapabilityId source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        kernel.AllocateRegion(owner, 128);
        provider.BeforeSource = () =>
        {
            provider.BeforeSource = null;
            Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
        };
        Assert.False(kernel.CreateVirtualDomain(owner, source, new(1, 4096)).IsSuccess);
        Assert.Equal(0, provider.CreateCalls);
        Assert.False(kernel.PlatformAuthority.HasUnresolvedVirtualDomainCreate(new(process.DomainId, owner)));
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
    }
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task ConcurrentModelDestroyAndTeardownKeepOriginalClosureInterlocked(bool teardownFirst, bool lostReply)
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 988, 9880);
        CapabilityId source = kernel.MintCapability(new DomainId(9880), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        provider.BeforeRevoke = () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
            if (lostReply) throw new IOException("controlled concurrent revoke reply loss");
        };
        KernelResult Destroy() => kernel.DestroyVirtualDomain(owner, domain.Domain, domain.ConfigureCapability);
        Task<KernelResult> first = Task.Run(() => teardownFirst ? kernel.TerminateProcess(owner) : Destroy());
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            KernelResult overlap = teardownFirst ? Destroy() : kernel.TerminateProcess(owner);
            Assert.False(overlap.IsSuccess);
            Assert.Equal(1, provider.RevokeCalls);
            Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        }
        finally { release.Set(); }
        KernelResult completed = await first.WaitAsync(TimeSpan.FromSeconds(15));
        provider.BeforeRevoke = null;
        if (lostReply)
        {
            Assert.False(completed.IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            KernelResult<ProcessTeardownSnapshot> observation = kernel.ObserveProcessTeardown(owner);
            Assert.True(observation.IsSuccess);
            Assert.False(observation.Value!.LocalReclaimCompleted);
            Assert.Equal(ProcessTeardownPhase.PlatformFaulted, observation.Value.Phase);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
            Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        }
        else
        {
            Assert.True(completed.IsSuccess, completed.Message);
            if (!teardownFirst) Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.QueryProcessTeardown(owner).IsSuccess); // Completed teardown removes its pending record.
            Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        }
        Assert.Equal(1, provider.RevokeCalls);
    }

    [Fact]
    public void ModelTeardownConsumesOriginalMappingBeforeProviderRevoke()
    {
        ObservedModelProvider provider = new ObservedModelProvider { CompleteRevokeBeforeReply = true };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 987, 9870);
        CapabilityId source = kernel.MintCapability(new DomainId(9870), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        CapabilityId cap = kernel.MintCapability(new DomainId(9870), owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        GuestRegionMapping mapping = kernel.MapGuestRegion(owner, domain.Domain, domain.MemoryCapability, cap,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read).Value!;
        VirtualDomainAuthority.Record local = Assert.Single(Records(kernel).ForOwner(owner));
        bool observed = false;
        provider.BeforeRevoke = () =>
        {
            observed = true;
            Assert.Empty(local.Mappings);
            Assert.Empty(local.OriginalMappings);
            Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
            Assert.False(kernel.MapGuestRegion(owner, domain.Domain, domain.MemoryCapability, cap,
                region.Handle, new(0, 4096), GuestMemoryAccess.Read).IsSuccess);
        };
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.True(observed);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Empty(local.OriginalMappings);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MissingModelMappingRouteRefusesProviderClosureUntilExactRepair(bool teardown)
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 986, 9860);
        CapabilityId source = kernel.MintCapability(new DomainId(9860), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        CapabilityId cap = kernel.MintCapability(new DomainId(9860), owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        GuestRegionMapping mapping = kernel.MapGuestRegion(owner, domain.Domain, domain.MemoryCapability, cap,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read).Value!;
        VirtualDomainAuthority.Record local = Assert.Single(Records(kernel).ForOwner(owner));
        local.Mappings.Remove(mapping.Mapping.MappingId);
        KernelResult Close() => teardown ? kernel.TerminateProcess(owner) :
            kernel.DestroyVirtualDomain(owner, domain.Domain, domain.ConfigureCapability);
        Assert.False(Close().IsSuccess);
        Assert.False(Close().IsSuccess);
        Assert.Equal(0, provider.RevokeCalls);
        Assert.Single(local.OriginalMappings);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        local.Mappings[mapping.Mapping.MappingId] = mapping;
        Assert.True(Close().IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Empty(local.OriginalMappings);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)]
    public void ModelMappingTupleDriftCannotReleaseOriginalOrOtherReservation(bool teardown, int fault)
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 985, 9850);
        CapabilityId source = kernel.MintCapability(new DomainId(9850), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain = kernel.CreateVirtualDomain(owner, source, new(1, 8192)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        OwnedRegion<int> other = kernel.AllocateRegion(owner, 4096).Value!;
        CapabilityId cap = kernel.MintCapability(new DomainId(9850), owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        GuestRegionMapping mapped = kernel.MapGuestRegion(owner, domain.Domain, domain.MemoryCapability, cap,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read).Value!;
        VirtualDomainAuthority.Record local = Assert.Single(Records(kernel).ForOwner(owner));
        object regions = Field(kernel.Regions, "_regions");
        object original = regions.GetType().GetProperty("Item")!.GetValue(regions, [region.Handle.RegionId])!;
        object unrelated = regions.GetType().GetProperty("Item")!.GetValue(regions, [other.Handle.RegionId])!;
        bool Reserved(object record) => (bool)record.GetType().GetProperty("PlatformMappingReserved")!.GetValue(record)!;
        Assert.True(Reserved(original));
        Assert.False(Reserved(unrelated));
        local.Mappings[mapped.Mapping.MappingId] = fault switch
        {
            0 => mapped with { Region = other.Handle },
            1 => mapped with { GuestRange = new(4096, 4096) },
            _ => mapped with { Mapping = mapped.Mapping with { Generation = new(2) } },
        };
        KernelResult Close() => teardown ? kernel.TerminateProcess(owner) :
            kernel.CloseGuestRegionMapping(owner, domain.Domain, domain.MemoryCapability, mapped.Mapping);
        Assert.False(Close().IsSuccess);
        Assert.False(Close().IsSuccess);
        Assert.True(Reserved(original));
        Assert.False(Reserved(unrelated));
        Assert.Equal(0, provider.RevokeCalls);
        Assert.Single(local.OriginalMappings);
        if (teardown) Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        local.Mappings[mapped.Mapping.MappingId] = mapped;
        Assert.True(Close().IsSuccess);
        Assert.False(Reserved(original));
        Assert.False(Reserved(unrelated));
        Assert.Empty(local.Mappings);
        Assert.Empty(local.OriginalMappings);
        Assert.Equal(teardown ? 1 : 0, provider.RevokeCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ModelFinalAdmissionAndPublicationRequireOriginalLocalOwner(bool afterEffect)
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 984, 9840);
        CapabilityId source = kernel.MintCapability(new DomainId(9840), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet created = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        VirtualDomainAuthority.Record local = Assert.Single(Records(kernel).ForOwner(owner));
        IDictionary originals = (global::System.Collections.IDictionary)Field(Records(kernel), "_originalRecords");
        void LoseOriginal()
        {
            provider.BeforeSource = null;
            originals.Remove(created.Domain.DomainId);
        }
        if (afterEffect) provider.AfterTransition = LoseOriginal;
        else provider.BeforeSource = LoseOriginal;
        Assert.False(kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).IsSuccess);
        Assert.Equal(afterEffect ? 1 : 0, provider.TransitionCalls);
        Assert.NotEqual(VirtualDomainState.Configured, local.State);
        Assert.False(kernel.StartVirtualDomain(owner, created.Domain, created.ExecuteCapability).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        provider.AfterTransition = null;
        provider.BeforeSource = null;
        originals[created.Domain.DomainId] = local; // exact original owner restoration; no permission/lease recreation
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Fact]
    public async Task ConcurrentPublicModelDestroyCannotRepeatOriginalRevoke()
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 983, 9830);
        CapabilityId source = kernel.MintCapability(new DomainId(9830), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet created = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        VirtualDomainAuthority.Record local = Assert.Single(Records(kernel).ForOwner(owner));
        IDictionary originals = (global::System.Collections.IDictionary)Field(kernel.PlatformAuthority, "_originalVirtualDomainOwners");
        PlatformAuthorityBridge.VirtualDomainBinding binding = local.ModelBinding!.Value;
        object original = originals[binding.BindingId]!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        provider.BeforeRevoke = () =>
        {
            Assert.False(Monitor.IsEntered(Field(kernel, "_platformMemoryUseGate")));
            Assert.False(Monitor.IsEntered(Field(kernel.PlatformAuthority, "_secureDomainLifecycleGate")));
            Assert.False(Monitor.IsEntered(Field(Records(kernel), "_gate")));
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        Task<KernelResult> first = Task.Run(() => kernel.DestroyVirtualDomain(owner, created.Domain, created.ConfigureCapability));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            KernelResult overlap = await Task.Run(() => kernel.DestroyVirtualDomain(owner, created.Domain, created.ConfigureCapability))
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(overlap.IsSuccess);
            Assert.Equal(1, provider.RevokeCalls);
            Assert.Same(original, originals[binding.BindingId]);
        }
        finally { release.Set(); }
        KernelResult completed = await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(completed.IsSuccess, completed.Message);
        Assert.Equal(VirtualDomainState.Closed, local.State);
        Assert.Equal(false, original.GetType().GetProperty("CloseInFlight")!.GetValue(original));
        Assert.False(originals.Contains(binding.BindingId));
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PublicModelDestroyAmbiguousRevokeNeverRepeatsOrReclaims(bool lostReply)
    {
        ObservedModelProvider provider = new ObservedModelProvider { CompleteRevokeBeforeReply = true };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 982, 9820);
        CapabilityId source = kernel.MintCapability(new DomainId(9820), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet created = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        if (lostReply) provider.BeforeRevoke = () => throw new IOException("controlled lost original revoke reply");
        else provider.RevokeFailure = PlatformAuthorityStatus.Faulted;
        Assert.False(kernel.DestroyVirtualDomain(owner, created.Domain, created.ConfigureCapability).IsSuccess);
        VirtualDomainAuthority.Record local = Assert.Single(Records(kernel).ForOwner(owner));
        IDictionary originals = (global::System.Collections.IDictionary)Field(kernel.PlatformAuthority, "_originalVirtualDomainOwners");
        object original = originals[local.ModelBinding!.Value.BindingId]!;
        Assert.Equal(false, original.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(original));
        provider.BeforeRevoke = null;
        provider.RevokeFailure = null;
        Assert.False(kernel.DestroyVirtualDomain(owner, created.Domain, created.ConfigureCapability).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Same(original, originals[local.ModelBinding.Value.BindingId]);
        Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void GuestMappingReleaseRefusalRetainsOriginalFactsForActualRetry(bool teardown)
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 981, 9810);
        CapabilityId createCap = kernel.MintCapability(new DomainId(9810), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet created = kernel.CreateVirtualDomain(owner, createCap, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).IsSuccess);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        CapabilityId cap = kernel.MintCapability(new DomainId(9810), owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        KernelResult<GuestRegionMapping> mapped = kernel.MapGuestRegion(owner, created.Domain, created.MemoryCapability,
            cap, region.Handle, new(0, 4096), GuestMemoryAccess.Read);
        Assert.True(mapped.IsSuccess, mapped.Message);
        GuestRegionMapping mapping = mapped.Value!;
        VirtualDomainAuthority.Record local = Assert.Single(Records(kernel).ForOwner(owner));
        object regions = Field(kernel.Regions, "_regions");
        object regionRecord = regions.GetType().GetProperty("Item")!.GetValue(regions, [region.Handle.RegionId])!;
        PropertyInfo generation = regionRecord.GetType().GetProperty("Generation")!;
        generation.SetValue(regionRecord, new RegionGeneration(region.Handle.Generation.Value + 1));
        KernelResult refused = teardown ? kernel.TerminateProcess(owner) :
            kernel.CloseGuestRegionMapping(owner, created.Domain, created.MemoryCapability, mapping.Mapping);
        Assert.False(refused.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, refused.Error);
        Assert.Equal(mapping, Assert.Single(local.Mappings.Values));
        Assert.True(local.MappingSources.ContainsKey(mapping.Mapping.MappingId));
        Assert.True((bool)regionRecord.GetType().GetProperty("PlatformMappingReserved")!.GetValue(regionRecord)!);
        Assert.Equal(0, provider.RevokeCalls);
        if (teardown) Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        generation.SetValue(regionRecord, region.Handle.Generation); // restore exact source; no mapping reconstruction or re-reservation
        KernelResult retry = teardown ? kernel.TerminateProcess(owner) :
            kernel.CloseGuestRegionMapping(owner, created.Domain, created.MemoryCapability, mapping.Mapping);
        Assert.True(retry.IsSuccess, $"{retry.Error}: {retry.Message}");
        Assert.Empty(local.Mappings);
        Assert.Empty(local.MappingSources);
        Assert.False((bool)regionRecord.GetType().GetProperty("PlatformMappingReserved")!.GetValue(regionRecord)!);
        Assert.Equal(teardown ? 1 : 0, provider.RevokeCalls);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)] [InlineData(3, true)]
    public void AcceptedModelClosureRetainsReceiptUntilFreshLocalCleanupCommit(int fault, bool destroy)
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 980, 9800);
        CapabilityId createCap = kernel.MintCapability(new DomainId(9800), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet created = kernel.CreateVirtualDomain(owner, createCap, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).IsSuccess);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        CapabilityId cap = kernel.MintCapability(new DomainId(9800), owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        KernelResult<GuestRegionMapping> mapped = kernel.MapGuestRegion(owner, created.Domain, created.MemoryCapability,
            cap, region.Handle, new(0, 4096), GuestMemoryAccess.Read);
        Assert.True(mapped.IsSuccess, mapped.Message);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle, new(new(9800), owner.Generation)));
        VirtualDomainAuthority.Record local = Assert.Single(Records(kernel).ForOwner(owner));
        PlatformAuthorityBridge.VirtualDomainBinding binding = local.ModelBinding!.Value;
        IDictionary originals = (global::System.Collections.IDictionary)Field(kernel.PlatformAuthority, "_originalVirtualDomainOwners");
        object original = originals[binding.BindingId]!;
        IDictionary localRecords = (global::System.Collections.IDictionary)Field(Records(kernel), "_records");
        int cleanupSourceReads = 0;
        provider.BeforeRevoke = () =>
        {
            Assert.Empty(local.Mappings);
            provider.BeforeSource = () =>
            {
                Assert.False(Monitor.IsEntered(Field(kernel, "_platformMemoryUseGate")));
                Assert.False(Monitor.IsEntered(Field(kernel.PlatformAuthority, "_secureDomainLifecycleGate")));
                Assert.False(Monitor.IsEntered(Field(Records(kernel), "_gate")));
                // First query completes the accepted revoke. The second belongs
                // to the actual local cleanup receipt consumer.
                if (++cleanupSourceReads != 2) return;
                if (fault == 3) localRecords.Remove(created.Domain.DomainId);
                else if (fault == 2) provider.ThrowSource = true;
                else provider.Incarnation = new(fault == 0 ? 2UL : 0UL);
            };
        };
        KernelResult refused = destroy ? kernel.DestroyVirtualDomain(owner, created.Domain, created.ConfigureCapability) :
            kernel.TerminateProcess(owner);
        Assert.False(refused.IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Same(original, originals[binding.BindingId]);
        Assert.Equal(true, original.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(original));
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        if (!destroy) Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        Assert.Equal(VirtualDomainState.Quarantined, local.State);
        Assert.Equal(fault == 3 ? 3 : 2, cleanupSourceReads);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
        provider.BeforeRevoke = null;
        provider.BeforeSource = null;
        provider.ThrowSource = false;
        provider.Incarnation = new(1); // controlled restoration of the exact original provider source
        if (fault == 3) localRecords[created.Domain.DomainId] = local;
        KernelResult retry = destroy ? kernel.DestroyVirtualDomain(owner, created.Domain, created.ConfigureCapability) :
            kernel.TerminateProcess(owner);
        Assert.True(retry.IsSuccess, $"{retry.Error}: {retry.Message}");
        Assert.Equal(1, provider.RevokeCalls);
        Assert.False(originals.Contains(binding.BindingId));
        if (destroy)
        {
            Assert.Equal(VirtualDomainState.Closed, local.State);
            Assert.False(kernel.QueryVirtualDomain(owner, created.Domain).IsSuccess);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1, provider.RevokeCalls);
        }
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ModelOwnerLeasePinsActualReclaimWhenLocalInventoryIsMissingOrClosed(bool closed)
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 979, 9790);
        CapabilityId source = kernel.MintCapability(new DomainId(9790), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        VirtualDomainAuthoritySet created = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        VirtualDomainAuthority.Record local = Assert.Single(Records(kernel).ForOwner(owner));
        IDictionary records = (global::System.Collections.IDictionary)Field(Records(kernel), "_records");
        if (closed) local.State = VirtualDomainState.Closed;
        else records.Remove(created.Domain.DomainId);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(0, provider.TransitionCalls);
        Assert.Equal(0, provider.RevokeCalls);
        Assert.True(kernel.PlatformAuthority.HasUnclosedModelDomainOwner(owner));
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
        if (closed) local.State = VirtualDomainState.Created;
        else records[created.Domain.DomainId] = local; // controlled local consumer repair
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.False(kernel.PlatformAuthority.HasUnclosedModelDomainOwner(owner));
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ModelOwnerIndexLossAfterCallbacksRefusesEffectPublicationOrReclaim(int stage)
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 978, 9780);
        CapabilityId source = kernel.MintCapability(new DomainId(9780), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        VirtualDomainAuthoritySet created = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        PlatformAuthorityBridge.VirtualDomainBinding binding = Assert.Single(Records(kernel).ForOwner(owner)).ModelBinding!.Value;
        IDictionary originals = (global::System.Collections.IDictionary)Field(kernel.PlatformAuthority, "_originalVirtualDomainOwners");
        object original = originals[binding.BindingId]!;
        void RemoveOwner() { provider.BeforeSource = null; originals.Remove(binding.BindingId); }
        if (stage == 0) provider.BeforeSource = RemoveOwner;
        if (stage == 1) provider.AfterTransition = () => provider.BeforeSource = RemoveOwner;
        if (stage == 2) provider.BeforeRevoke = RemoveOwner;
        if (stage != 2)
        {
            Assert.False(kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).IsSuccess);
            Assert.Equal(stage, provider.TransitionCalls);
        }
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(stage == 2 ? 1 : 0, provider.RevokeCalls);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
        originals[binding.BindingId] = original; // controlled exact index repair
        provider.BeforeSource = null; provider.AfterTransition = null; provider.BeforeRevoke = null;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.False(originals.Contains(binding.BindingId));
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ModelAdmissionRequiresCanonicalOriginalOwnerEvenWithMatchingProviderSource(bool changeSource)
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 977, 9770);
        CapabilityId source = kernel.MintCapability(new DomainId(9770), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        VirtualDomainAuthoritySet created = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        PlatformAuthorityBridge.VirtualDomainBinding binding = Assert.Single(Records(kernel).ForOwner(owner)).ModelBinding!.Value;
        IDictionary routes = (global::System.Collections.IDictionary)Field(kernel.PlatformAuthority, "_virtualDomainBindings");
        IDictionary originals = (global::System.Collections.IDictionary)Field(kernel.PlatformAuthority, "_originalVirtualDomainOwners");
        object original = originals[binding.BindingId]!;
        object foreign = typeof(object).GetMethod("MemberwiseClone", global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!.Invoke(original, null)!;
        if (changeSource)
        {
            provider.Incarnation = new(2);
            foreign.GetType().GetProperty("ProviderIncarnation")!.SetValue(foreign, provider.Incarnation);
        }
        routes[binding.BindingId] = foreign;
        Assert.False(kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).IsSuccess);
        Assert.Equal(0, provider.TransitionCalls);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(0, provider.RevokeCalls);
        Assert.Same(original, originals[binding.BindingId]);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
        // Controlled repair proves the original cleanup consumer, not durable recovery.
        routes[binding.BindingId] = original;
        provider.Incarnation = new(1);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.False(originals.Contains(binding.BindingId));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ModelTransitionContinuationCannotMutateForeignSourcePinsOrInterlock(int fault)
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 976, 9760);
        CapabilityId source = kernel.MintCapability(new DomainId(9760), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        VirtualDomainAuthoritySet created = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        VirtualDomainAuthority.Record local = Assert.Single(Records(kernel).ForOwner(owner));
        PlatformAuthorityBridge.VirtualDomainBinding binding = local.ModelBinding!.Value;
        IDictionary roots = (global::System.Collections.IDictionary)Field(kernel.PlatformAuthority, "_virtualDomainBindings");
        object original = roots[binding.BindingId]!;
        object foreign = null!;
        provider.AfterTransition = () =>
        {
            foreign = typeof(object).GetMethod("MemberwiseClone", global::System.Reflection.BindingFlags.Instance |
                global::System.Reflection.BindingFlags.NonPublic)!.Invoke(original, null)!;
            if (fault == 0) foreign.GetType().GetProperty("ProviderIncarnation")!.SetValue(foreign, new PlatformProviderIncarnation(2));
            if (fault == 1) foreign.GetType().GetProperty("CreatedInEpoch")!.SetValue(foreign, new PlatformBackendEpoch(2));
            if (fault == 2) foreign.GetType().GetProperty("Binding")!.SetValue(foreign,
                binding with { BindingId = new(binding.BindingId.Value + 100) });
            roots[binding.BindingId] = foreign;
        };
        Assert.False(kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).IsSuccess);
        Assert.Equal(VirtualDomainState.Created, local.State);
        Assert.Equal(true, foreign.GetType().GetProperty("TransitionInFlight")!.GetValue(foreign));
        Assert.Equal(false, foreign.GetType().GetProperty("Quarantined")!.GetValue(foreign));
        Assert.Equal(false, foreign.GetType().GetProperty("TransitionMayHaveEffect")!.GetValue(foreign));
        Assert.Equal(false, original.GetType().GetProperty("TransitionInFlight")!.GetValue(original));
        Assert.Equal(true, original.GetType().GetProperty("TransitionMayHaveEffect")!.GetValue(original));
        Assert.Equal(PlatformVirtualDomainTransition.Configure, original.GetType().GetProperty("TransitionIntent")!.GetValue(original));
        PlatformAuthorityResult observation = Assert.IsType<PlatformAuthorityResult>(original.GetType().GetProperty("TransitionObservation")!.GetValue(original));
        Assert.True(observation.IsSuccess);
        Assert.Null(foreign.GetType().GetProperty("TransitionObservation")!.GetValue(foreign));
        Assert.False(kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.TransitionCalls);
        Assert.Equal(0, provider.RevokeCalls);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.Denied)]
    [InlineData(PlatformAuthorityStatus.Stale)]
    [InlineData(PlatformAuthorityStatus.NotAccepted)]
    public void FailedOriginalModelRevokeRetainsRawOutcomeWithoutRepeatOrReclaim(PlatformAuthorityStatus status)
    {
        ObservedModelProvider provider = new ObservedModelProvider { CompleteRevokeBeforeReply = true, RevokeFailure = status };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 973, 9730);
        CapabilityId source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        _ = kernel.AllocateRegion(owner, 4096).Value!;
        Assert.True(kernel.CreateVirtualDomain(owner, source, new(1, 4096)).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        IDictionary records = (global::System.Collections.IDictionary)Field(kernel.PlatformAuthority, "_virtualDomainBindings");
        object original = Assert.Single(records.Values.Cast<object>());
        object raw = original.GetType().GetProperty("CloseObservation")!.GetValue(original)!;
        Assert.Equal(status, raw.GetType().GetProperty("Status")!.GetValue(raw));
        Assert.Equal(true, original.GetType().GetProperty("CloseMayHaveEffect")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("CloseInFlight")!.GetValue(original));
        int transitions = provider.TransitionCalls;
        provider.RevokeFailure = null;
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Equal(transitions, provider.TransitionCalls);
        Assert.NotEqual(ProcessState.Exited, process.State);
        Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)]
    public void ModelRevokeRetainsOriginalFactAcrossOwnerLossAndPinsActualTeardown(int ownerFault, bool lostReply)
    {
        ObservedModelProvider provider = new ObservedModelProvider { CompleteRevokeBeforeReply = true };
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 974, 9740);
        CapabilityId source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        _ = kernel.AllocateRegion(owner, 4096).Value!;
        Assert.True(kernel.CreateVirtualDomain(owner, source, new(1, 4096)).IsSuccess);
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary records = (global::System.Collections.IDictionary)Field(kernel.PlatformAuthority, "_virtualDomainBindings");
        object key = Assert.Single(records.Keys.Cast<object>());
        object original = records[key]!;
        provider.BeforeRevoke = () =>
        {
            Assert.False(Monitor.IsEntered(Field(kernel.PlatformAuthority, "_secureDomainLifecycleGate")));
            Assert.Equal(true, original.GetType().GetProperty("CloseMayHaveEffect")!.GetValue(original));
            if (ownerFault == 0) records.Remove(key);
            if (ownerFault == 1)
            {
                object foreign = original.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                foreign.GetType().GetProperty("ProviderIncarnation")!.SetValue(foreign, new PlatformProviderIncarnation(2));
                records[key] = foreign;
            }
            if (ownerFault == 2) Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
            if (lostReply) throw new IOException("Model revoke reply lost after effect");
        };
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(!lostReply, original.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("CloseInFlight")!.GetValue(original));
        Assert.Equal(lostReply, original.GetType().GetProperty("CloseObservation")!.GetValue(original) is null);
        if (ownerFault == 2)
        {
            object current = records[key]!;
            Assert.Equal(true, current.GetType().GetProperty("CloseMayHaveEffect")!.GetValue(current));
            Assert.Equal(false, current.GetType().GetProperty("CloseInFlight")!.GetValue(current));
            Assert.Equal(!lostReply, current.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(current));
        }
        provider.BeforeRevoke = null;
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ModelCreateSourcePauseKeepsPendingAndResetRefusesOriginalEffect(bool reset)
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 972, 9720);
        CapabilityId source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        using ManualResetEventSlim entered = new ManualResetEventSlim(); using ManualResetEventSlim release = new ManualResetEventSlim();
        provider.BeforeSource = () =>
        {
            provider.BeforeSource = null;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(6))) throw new TimeoutException("Model source pause expired");
        };
        Task<KernelResult<VirtualDomainAuthoritySet>> work = Task.Run(() => kernel.CreateVirtualDomain(owner, source, new(1, 4096)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            await Task.Run(() =>
            {
                Assert.False(kernel.CreateVirtualDomain(owner, source, new(1, 4096)).IsSuccess);
                Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
                Assert.Equal(0, provider.CreateCalls);
                if (reset) Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
            }).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { release.Set(); }
        Assert.Equal(!reset, (await work).IsSuccess);
        Assert.Equal(reset ? 0 : 1, provider.CreateCalls);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        if (reset) Assert.False(kernel.PlatformAuthority.HasUnresolvedVirtualDomainCreate(new(process.DomainId, owner)));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void ActualModelCreateSourceRefusalRetainsCleanupBindingBeforeLateFailure(int fault)
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 971, 9710);
        CapabilityId source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        _ = kernel.AllocateRegion(owner, 4096).Value!;
        if (fault == 0) provider.ThrowSource = true;
        provider.AfterCreate = () =>
        {
            if (fault == 1) provider.ThrowSource = true;
            if (fault == 2) provider.Incarnation = new(2);
            if (fault == 3) Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 4) throw new IOException("Model create reply lost");
        };
        Assert.False(kernel.CreateVirtualDomain(owner, source, new(1, 4096)).IsSuccess);
        Assert.Equal(fault == 0 ? 0 : 1, provider.CreateCalls);
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary bridge = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_virtualDomainBindings", flags)!.GetValue(kernel.PlatformAuthority)!;
        VirtualDomainAuthority local = (VirtualDomainAuthority)typeof(RuntimeKernel).GetField("_virtualDomains", flags)!.GetValue(kernel)!;
        if (fault is 1 or 2 or 3)
        {
            object record = Assert.Single(bridge.Values.Cast<object>());
            Assert.Equal(new PlatformProviderIncarnation(1), record.GetType().GetProperty("ProviderIncarnation")!.GetValue(record));
            VirtualDomainAuthority.Record retained = Assert.Single(local.ForOwner(owner));
            Assert.NotNull(retained.ModelBinding);
            Assert.Equal(VirtualDomainState.Quarantined, retained.State);
        }
        else { Assert.Empty(bridge); Assert.Empty(local.ForOwner(owner)); }
        provider.ThrowSource = false;
        provider.AfterCreate = null;
        Assert.Equal(fault is 0 or 1, kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(fault == 1 ? 1 : 0, provider.RevokeCalls);
        Assert.Equal(fault is 0 or 1 ? RegionState.Released : RegionState.Owned,
            Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ActualModelKnownRevokeRetryRetainsSourceOrPinsUnknownOutcome(int fault)
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 970, 9700);
        CapabilityId source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.CreateVirtualDomain(owner, source, new(1, 4096)).IsSuccess);
        _ = kernel.AllocateRegion(owner, 4096).Value!;
        provider.BeforeRevoke = () =>
        {
            if (fault == 0) provider.ThrowSource = true;
            if (fault == 1) provider.Incarnation = new(2);
            if (fault == 2) Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 3) throw new IOException("model reply lost");
        };
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        provider.ThrowSource = false;
        provider.BeforeRevoke = null;
        Assert.Equal(fault == 0, kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Equal(fault == 0 ? RegionState.Released : RegionState.Owned,
            Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void CreationFaultsKeepKnownModelCleanupOrUnknownTuplePin(int fault)
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 960, 9600);
        CapabilityId source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        if (fault == 0) provider.AfterCreate = () => Assert.True(kernel.RevokeCapability(source).IsSuccess);
        if (fault == 1) provider.AfterCreate = () =>
        {
            Assert.Equal(KernelError.PlatformFaulted, kernel.TerminateProcess(owner).Error);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        };
        if (fault == 2) provider.AfterCreate = () => throw new InvalidOperationException("controlled lost model receipt");
        if (fault == 3) provider.CreateFailure = PlatformAuthorityStatus.Denied;
        if (fault == 4) provider.AfterCreate = () => Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
        if (fault == 5) provider.MalformedLease = true;
        if (fault == 6) SetNonce(kernel, () => { Assert.True(kernel.RevokeCapability(source).IsSuccess); return Guid.NewGuid(); });
        KernelResult<VirtualDomainAuthoritySet> created = kernel.CreateVirtualDomain(owner, source, new(1, 4096));
        Assert.Equal(1, provider.CreateCalls);
        if (fault is 7 or 8)
        {
            Assert.True(created.IsSuccess, created.Message);
            if (fault == 7) provider.RevokeFailure = PlatformAuthorityStatus.Faulted;
            else provider.BeforeRevoke = () => throw new InvalidOperationException("controlled lost revoke receipt");
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1, provider.RevokeCalls);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        }
        else Assert.False(created.IsSuccess);
        provider.AfterCreate = null;
        provider.CreateFailure = null;
        provider.RevokeFailure = null;
        provider.BeforeRevoke = null;
        if (fault is 0 or 1 or 6)
        {
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(ProcessState.Exited, process.State);
            Assert.Equal(1, provider.RevokeCalls);
            Assert.Equal(KernelError.StaleHandle, kernel.QueryProcessTeardown(owner).Error);
        }
        else
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
            Assert.Equal(fault is 7 or 8 ? 1 : 0, provider.RevokeCalls);
            Assert.Equal(1, provider.CreateCalls);
        }
        Assert.Equal(PlatformFeatureAvailability.ModelOnly,
            kernel.QueryPlatformFeatures().Resolve(PlatformFeatureFamily.VirtualizationDomains).Availability);
    }

    [Fact]
    public void PartialCapabilitiesCannotAdmitEffectBeforeActualPublication()
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 961, 9610);
        CapabilityId source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        int nonceCalls = 0;
        SetNonce(kernel, () =>
        {
            if (++nonceCalls == 2)
            {
                VirtualDomainAuthority.Record record = Assert.Single(Records(kernel).ForOwner(owner));
                Assert.False(record.AuthorityPublished);
                Assert.Single(record.Capabilities);
                Assert.Equal(KernelError.PlatformBindingActive,
                    kernel.ConfigureVirtualDomain(owner, record.Handle, record.Capabilities[0]).Error);
                Assert.Equal(0, provider.TransitionCalls);
            }
            return Guid.NewGuid();
        });
        KernelResult<VirtualDomainAuthoritySet> created = kernel.CreateVirtualDomain(owner, source, new(1, 4096));
        Assert.True(created.IsSuccess, created.Message);
        Assert.True(Assert.Single(Records(kernel).ForOwner(owner)).AuthorityPublished);
        Assert.True(kernel.ConfigureVirtualDomain(owner, created.Value!.Domain, created.Value.ConfigureCapability).IsSuccess);
        Assert.Equal(1, provider.TransitionCalls);
        Assert.True(kernel.DestroyVirtualDomain(owner, created.Value.Domain, created.Value.ConfigureCapability).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls); // a closed model record is not reopened by teardown
    }

    [Fact]
    public async Task ConcurrentStopDuringModelCallbackCannotReclaimOrPublishLateAuthority()
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 962, 9620);
        CapabilityId source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        provider.AfterCreate = () =>
        {
            foreach ((object Owner, string Field) pair in new (object Owner, string Field)[]
            {
                (kernel, "_platformMemoryUseGate"), (kernel.PlatformAuthority, "_secureDomainLifecycleGate"),
                (kernel.CapabilityAuthority, "_gate")
            })
                Assert.False(Monitor.IsEntered(Field(pair.Owner, pair.Field)));
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
        };
        Task<KernelResult<VirtualDomainAuthoritySet>> work = Task.Run(() => kernel.CreateVirtualDomain(owner, source, new(1, 4096)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            Assert.Equal(KernelError.PlatformBindingActive, kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Error);
            Assert.Equal(KernelError.PlatformFaulted, kernel.TerminateProcess(owner).Error);
            Assert.Equal(0, provider.RevokeCalls);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        }
        finally { release.Set(); }
        Assert.False((await work.WaitAsync(TimeSpan.FromSeconds(15))).IsSuccess);
        Assert.False(Assert.Single(Records(kernel).ForOwner(owner)).AuthorityPublished);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.CreateCalls);
        Assert.Equal(1, provider.RevokeCalls);
    }

    [Fact]
    public void ExactSourceAndProfileRefusalDoNotStartModelEffect()
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 963, 9630);
        CapabilityId source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.False(kernel.CreateVirtualDomain(owner, source, new(0, 4096)).IsSuccess);
        Assert.False(kernel.CreateVirtualDomain(owner, source, new(257, 4096)).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        (SingProcess _, ProcessHandle replacement) = TestFixtures.Create(kernel, 963, 9630, generation: 2);
        Assert.False(kernel.CreateVirtualDomain(replacement, source, new(1, 4096)).IsSuccess);
        Assert.Equal(0, provider.CreateCalls);
    }

    [Fact]
    public void ResetDuringModelRevokeBalancesReplacementInterlockWithoutClosureClaim()
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 964, 9640);
        CapabilityId source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.CreateVirtualDomain(owner, source, new(1, 4096)).IsSuccess);
        provider.BeforeRevoke = () => Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        IDictionary records = (global::System.Collections.IDictionary)Field(kernel.PlatformAuthority, "_virtualDomainBindings");
        Assert.Single(records.Values.Cast<object>());
        foreach (object? value in records.Values)
        {
            Assert.False((bool)value!.GetType().GetProperty("CloseInFlight")!.GetValue(value)!);
            Assert.True((bool)value.GetType().GetProperty("Quarantined")!.GetValue(value)!);
        }
        provider.BeforeRevoke = null;
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        Assert.Equal(1, provider.RevokeCalls);
    }

    [Fact]
    public void HostModelStaleRevokeCannotRemoveCurrentLease()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        PlatformDomainIdentity owner = new PlatformDomainIdentity(new DomainId(9650), new ProcessHandle(new ProcessId(965), 1));
        PlatformProviderVirtualDomainLease lease = provider.CreateVirtualDomain(owner, new(1, 4096)).Value!;
        Assert.Equal(PlatformAuthorityStatus.Stale,
            provider.RevokeVirtualDomain(lease with { Generation = new(lease.Generation.Value + 1) }).Status);
        Assert.Equal(PlatformAuthorityStatus.Stale,
            provider.RevokeVirtualDomain(lease with { Owner = owner with { DomainId = new(9651) } }).Status);
        Assert.True(provider.TransitionVirtualDomain(lease, PlatformVirtualDomainTransition.Configure).IsSuccess);
        Assert.True(provider.RevokeVirtualDomain(lease).IsSuccess);
    }

    [Fact]
    public async Task ConcurrentHostModelCreatesKeepUniqueLeaseOwnerTuples()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        PlatformProviderVirtualDomainLease[] leases = await Task.WhenAll(Enumerable.Range(1, 32).Select(i => Task.Run(() =>
        {
            PlatformDomainIdentity owner = new PlatformDomainIdentity(new DomainId((ulong)(9660 + i)),
                new ProcessHandle(new ProcessId((ulong)(966 + i)), 1));
            PlatformAuthorityResult<PlatformProviderVirtualDomainLease> created = provider.CreateVirtualDomain(owner, new(1, 4096));
            Assert.True(created.IsSuccess);
            Assert.Equal(owner, created.Value!.Owner);
            return created.Value;
        })));
        Assert.Equal(32, leases.Select(x => x.LeaseId).Distinct().Count());
        foreach (PlatformProviderVirtualDomainLease lease in leases) Assert.True(provider.RevokeVirtualDomain(lease).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task PendingModelTransitionPreventsClosureAndLateStatePublication(int step)
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 970, 9700);
        CapabilityId source = kernel.MintCapability(new DomainId(9700), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet created = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        if (step > 0) Assert.True(kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).IsSuccess);
        if (step > 1) Assert.True(kernel.StartVirtualDomain(owner, created.Domain, created.ExecuteCapability).IsSuccess);
        if (step > 2) Assert.True(kernel.ParkVirtualDomain(owner, created.Domain, created.ExecuteCapability).IsSuccess);
        VirtualDomainAuthority.Record local = Assert.Single(Records(kernel).ForOwner(owner));
        int before = provider.TransitionCalls;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        provider.AfterTransition = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); };
        KernelResult Transition() => step == 0 ? kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability) :
            step == 1 ? kernel.StartVirtualDomain(owner, created.Domain, created.ExecuteCapability) :
            step == 2 ? kernel.ParkVirtualDomain(owner, created.Domain, created.ExecuteCapability) :
            kernel.ResumeVirtualDomain(owner, created.Domain, created.ExecuteCapability);
        Task<KernelResult> work = Task.Run(Transition);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            Assert.Equal(KernelError.PlatformBindingActive, Transition().Error);
            Assert.Equal(KernelError.PlatformBindingActive, kernel.PlatformAuthority.RevokeVirtualDomain(local.ModelBinding!.Value).Error);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
            Assert.Equal(0, provider.RevokeCalls);
        }
        finally { release.Set(); }
        Assert.False((await work.WaitAsync(TimeSpan.FromSeconds(15))).IsSuccess);
        provider.AfterTransition = null;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(VirtualDomainState.Closed, local.State);
        Assert.Equal(1, provider.RevokeCalls);
        Assert.Equal(before + 2, provider.TransitionCalls); // pending effect then actual cleanup drain
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ModelUnknownTransitionRequiresContainmentConsumerOrKeepsPin(int fault)
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 971, 9710);
        CapabilityId source = kernel.MintCapability(new DomainId(9710), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet created = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).IsSuccess);
        if (fault == 0) provider.AfterTransition = () => throw new InvalidOperationException("controlled lost transition receipt");
        if (fault == 1) provider.TransitionFailure = PlatformAuthorityStatus.Denied;
        if (fault == 2) provider.AfterTransition = () => Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
        if (fault == 3) provider.TransitionFailure = PlatformAuthorityStatus.NotAccepted;
        Assert.False(kernel.StartVirtualDomain(owner, created.Domain, created.ExecuteCapability).IsSuccess);
        VirtualDomainAuthority.Record local = Assert.Single(Records(kernel).ForOwner(owner));
        Assert.Equal(fault == 2 ? VirtualDomainState.Quarantined : VirtualDomainState.Configured, local.State);
        provider.AfterTransition = null;
        provider.TransitionFailure = null;
        IDictionary bridgeRecords = (global::System.Collections.IDictionary)Field(kernel.PlatformAuthority, "_virtualDomainBindings");
        foreach (object? value in bridgeRecords.Values)
            Assert.False((bool)value!.GetType().GetProperty("TransitionInFlight")!.GetValue(value)!);
        Assert.False(kernel.StartVirtualDomain(owner, created.Domain, created.ExecuteCapability).IsSuccess);
        if (fault == 3)
        {
            Assert.True(kernel.TerminateProcess(owner).IsSuccess); // explicit pre-admission refusal, real model cleanup
            Assert.Equal(1, provider.RevokeCalls);
        }
        else
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
            Assert.Equal(0, provider.RevokeCalls);
        }
    }

    [Fact]
    public void ModelSourceRevokeDuringCallbackCannotCommitStateOrReplayEffect()
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 972, 9720);
        CapabilityId source = kernel.MintCapability(new DomainId(9720), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet created = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).IsSuccess);
        provider.AfterTransition = () => Assert.True(kernel.RevokeCapability(created.ExecuteCapability).IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, kernel.StartVirtualDomain(owner, created.Domain, created.ExecuteCapability).Error);
        Assert.Equal(VirtualDomainState.Configured, Assert.Single(Records(kernel).ForOwner(owner)).State);
        int effects = provider.TransitionCalls;
        Assert.False(kernel.StartVirtualDomain(owner, created.Domain, created.ExecuteCapability).IsSuccess);
        Assert.Equal(effects, provider.TransitionCalls);
        provider.AfterTransition = null;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeCalls);
    }

    [Fact]
    public void UnknownModelTransitionAndRevokedSourceRefuseBeforeCallback()
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 973, 9730);
        CapabilityId source = kernel.MintCapability(new DomainId(9730), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet created = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        VirtualDomainAuthority.Record local = Assert.Single(Records(kernel).ForOwner(owner));
        Assert.Equal(KernelError.PlatformDenied,
            kernel.PlatformAuthority.TransitionVirtualDomain(local.ModelBinding!.Value, (PlatformVirtualDomainTransition)999).Error);
        Assert.True(kernel.RevokeCapability(created.ConfigureCapability).IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).Error);
        Assert.Equal(0, provider.TransitionCalls);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void ModelTransitionOriginalSourceRefusalSeparatesKnownAndUnknownEffect(int fault)
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 974, 9740);
        CapabilityId source = kernel.MintCapability(new DomainId(9740), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet created = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        VirtualDomainAuthority.Record local = Assert.Single(Records(kernel).ForOwner(owner));
        VirtualDomainState before = local.State;
        if (fault == 0) provider.ThrowSource = true;
        if (fault == 1) provider.Incarnation = new(2);
        if (fault == 2) provider.AfterTransition = () => provider.ThrowSource = true;
        if (fault == 3) provider.AfterTransition = () => provider.Incarnation = new(2);
        if (fault == 4) provider.AfterTransition = () => throw new IOException("lost model effect response");
        Assert.False(kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).IsSuccess);
        Assert.Equal(before, local.State);
        Assert.Equal(fault < 2 ? 0 : 1, provider.TransitionCalls);
        provider.AfterTransition = null;
        provider.ThrowSource = false;
        if (fault is 1 or 3 or 4)
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
            Assert.Equal(0, provider.RevokeCalls);
        }
        else
        {
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(VirtualDomainState.Closed, local.State);
            Assert.Equal(1, provider.RevokeCalls);
            Assert.Equal(fault == 0 ? 1 : 2, provider.TransitionCalls);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ModelSourceCallbackRevokeWinsFinalInvocationAdmission(bool reset)
    {
        ObservedModelProvider provider = new ObservedModelProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 975, 9750);
        CapabilityId source = kernel.MintCapability(new DomainId(9750), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet created = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        provider.BeforeSource = () =>
        {
            provider.BeforeSource = null;
            if (reset) Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
            else Assert.True(kernel.RevokeCapability(created.ConfigureCapability).IsSuccess);
        };
        Assert.False(kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).IsSuccess);
        Assert.Equal(0, provider.TransitionCalls);
        if (reset)
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
            Assert.Equal(0, provider.RevokeCalls);
        }
        else
        {
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1, provider.RevokeCalls);
        }
    }

    private static object Field(object owner, string name) =>
        owner.GetType().GetField(name, global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!.GetValue(owner)!;
    private static VirtualDomainAuthority Records(RuntimeKernel kernel) => (VirtualDomainAuthority)Field(kernel, "_virtualDomains");
    private static void SetNonce(RuntimeKernel kernel, Func<Guid> factory) =>
        typeof(CapabilityAuthority).GetField("_nonceFactory", global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!.SetValue(kernel.CapabilityAuthority, factory);

    private sealed class ObservedModelProvider : IPlatformAuthorityProvider, IPlatformVirtualizationProvider, IPlatformFeatureProvider, IPlatformProviderIncarnationSource
    {
        internal readonly HostPlatformAuthorityProvider _inner = new();
        public PlatformProviderIncarnation Incarnation { get; set; } = new(1);
        public bool ThrowSource { get; set; }
        public Action? BeforeSource { get; set; }
        public PlatformProviderIncarnation CurrentIncarnation
        {
            get
            {
                BeforeSource?.Invoke();
                return ThrowSource ? throw new IOException("Model source unavailable") : Incarnation;
            }
        }
        public Action? AfterCreate { get; set; }
        public Action? BeforeRevoke { get; set; }
        public bool CompleteRevokeBeforeReply { get; set; }
        public Action? AfterTransition { get; set; }
        public PlatformAuthorityStatus? TransitionFailure { get; set; }
        public PlatformAuthorityStatus? CreateFailure { get; set; }
        public PlatformAuthorityStatus? RevokeFailure { get; set; }
        public bool MalformedLease { get; set; }
        public bool MalformedRefusal { get; set; }
        public int CreateCalls { get; private set; }
        public int RevokeCalls { get; private set; }
        public int TransitionCalls { get; private set; }
        public PlatformProviderDescriptor Descriptor => _inner.Descriptor;
        public PlatformFeatureManifest QueryFeatures() => _inner.QueryFeatures();
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject) => _inner.BindDomain(subject);
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) => _inner.RevokeDomain(lease);
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(PlatformProviderDomainLease lease,
            PlatformRegionIdentity region, PlatformMemoryAccess access) => _inner.MapOwnedRegion(lease, region, access);
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease lease,
            PlatformRegionRevocationPolicy policy) => _inner.RevokeRegionMapping(lease, policy);
        public PlatformAuthorityResult<PlatformProviderVirtualDomainLease> CreateVirtualDomain(
            PlatformDomainIdentity owner, PlatformVirtualDomainProfile profile)
        {
            CreateCalls++;
            PlatformAuthorityResult<PlatformProviderVirtualDomainLease> result = _inner.CreateVirtualDomain(owner, profile);
            AfterCreate?.Invoke();
            if(MalformedRefusal)return new(PlatformAuthorityStatus.NotAccepted,result.Value,"Malformed refusal carries an effect lease");
            if (CreateFailure is { } status) return PlatformAuthorityResult<PlatformProviderVirtualDomainLease>.Fail(status, "controlled unknown creation");
            return MalformedLease ? PlatformAuthorityResult<PlatformProviderVirtualDomainLease>.Ok(
                result.Value! with { LeaseId = new(0) }) : result;
        }
        public PlatformAuthorityResult TransitionVirtualDomain(PlatformProviderVirtualDomainLease lease, PlatformVirtualDomainTransition transition)
        {
            TransitionCalls++;
            if (TransitionFailure == PlatformAuthorityStatus.NotAccepted)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted, "controlled refusal before model mutation");
            PlatformAuthorityResult result = _inner.TransitionVirtualDomain(lease, transition);
            AfterTransition?.Invoke();
            return TransitionFailure is { } status ? PlatformAuthorityResult.Fail(status, "controlled unknown model transition") : result;
        }
        public PlatformAuthorityResult RevokeVirtualDomain(PlatformProviderVirtualDomainLease lease)
        {
            RevokeCalls++;
            PlatformAuthorityResult completed = CompleteRevokeBeforeReply ? _inner.RevokeVirtualDomain(lease) : default;
            BeforeRevoke?.Invoke();
            return RevokeFailure is { } status ? PlatformAuthorityResult.Fail(status, "controlled model closure refusal")
                : CompleteRevokeBeforeReply ? completed : _inner.RevokeVirtualDomain(lease);
        }
    }
}
