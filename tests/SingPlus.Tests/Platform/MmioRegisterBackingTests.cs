using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;
namespace SingPlus.Tests.Platform;

public sealed partial class MmioRegisterBackingTests
{
    public interface IRuntime : INeutralDomainRuntime,INeutralOriginalMmioRegistersProvider { }
    public class Spy : DispatchProxy
    {
        public NeutralDomainRuntimeFacade Inner=new();public bool LoseAccess,LoseClose;public int Writes,Reads,Maps,Closes;
        public Action? BeforeAccess;
        public Action? AfterAccess;
        public Action? AfterMap;
        public bool Malformed;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(LoseAccess && method!.Name=="ResolveMmio32" || LoseClose && method!.Name=="ResolveMmioClose")throw new IOException("Original lookup lost");
            if(method!.Name=="AccessMmio32")BeforeAccess?.Invoke();
            var raw=method.Invoke(Inner,args);
            if(method.Name=="AccessMmio32")
            {AfterAccess?.Invoke();if(Malformed && raw is NeutralOriginalMmio32Observation value)raw=value with {Value=value.Value+1};}
            if(method.Name=="MapMmio"){Maps++;AfterMap?.Invoke();}
            if(method.Name=="AccessMmio32")
            { if(((NeutralOriginalMmio32Request)args![0]!).Write)Writes++;else Reads++;if(LoseAccess)throw new IOException("Original access reply lost after effect"); }
            if(method.Name=="CloseMmio"){Closes++;if(LoseClose)throw new IOException("Original Close reply lost after effect");}
            return raw;
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalRegistersExecuteAndRetainUseChargeUntilExactClose(bool loss)
    {
        var proxy=DispatchProxy.Create<IRuntime,Spy>();var spy=(Spy)(object)proxy;var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(proxy));
        var owner=TestFixtures.Create(kernel,3031,30310).Handle;var foreign=TestFixtures.Create(kernel,3032,30320).Handle;var admin=TestFixtures.Create(kernel,3033,30330).Handle;
        var budgetCap=kernel.MintCapability(new(30330),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(admin,budgetCap,owner,"registers",[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]).Value!.ProcessBudget;
        var foreignBudget=kernel.AdmitProcessBudget(admin,budgetCap,foreign,"foreign-registers",[new(ServiceBudgetDimension.OwnedMemoryBytes,32)]).Value!.ProcessBudget;
        var buffer=kernel.AllocateBuffer<byte>(owner,64).Value!;var peer=kernel.AllocateBuffer<byte>(foreign,32).Value!;
        var root=kernel.BindPlatformDomain(owner).Value;
        var deviceCap=kernel.MintCapability(new(30310),owner,ResourceKind.Device,"device/registers",CapabilityRights.Configure|CapabilityRights.Read|CapabilityRights.Write).Value!.CapabilityId;
        var device=kernel.BindPlatformDevice(owner,root,deviceCap,PlatformDeviceRights.Configure|PlatformDeviceRights.Read|PlatformDeviceRights.Write).Value;
        var cap=kernel.MintCapability(new(30310),owner,ResourceKind.MmioRegion,CapabilityResourceIds.MmioRegion("device/registers","bank",64),CapabilityRights.Map|CapabilityRights.Read|CapabilityRights.Write).Value!.CapabilityId;
        var mapped=kernel.BindPlatformMmioBacking(owner,device,cap,0,64,PlatformMmioAccess.Read|PlatformMmioAccess.Write,buffer);Assert.True(mapped.IsSuccess,mapped.Message);var map=mapped.Value;
        var use=Assert.Single(kernel.Regions.SnapshotUses());Assert.False(kernel.ReleaseRegionUse(owner,use.Handle).IsSuccess);
        var original=new PlatformMmio32Invocation(map,4,true,42);spy.LoseAccess=loss;
        var written=kernel.AccessPlatformMmio32(owner,original);
        if(loss)
        {
            Assert.False(written.IsSuccess);Assert.False(kernel.AccessPlatformMmio32(owner,original).IsSuccess);
            Assert.False(kernel.ResolvePlatformMmio32(owner,original).IsSuccess);Assert.Equal(1,spy.Writes);
            Assert.Equal(RegionUseState.Active,Assert.Single(kernel.Regions.SnapshotUses()).State);spy.LoseAccess=false;
            written=kernel.ResolvePlatformMmio32(owner,original);
        }
        Assert.True(written.IsSuccess,written.Message);Assert.Equal(42U,written.Value);
        var read=kernel.AccessPlatformMmio32(owner,new(map,4,false));Assert.True(read.IsSuccess,read.Message);Assert.Equal(42U,read.Value);
        var copy=(PlatformMmio32Invocation)typeof(object).GetMethod("MemberwiseClone",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(original,null)!;
        Assert.False(kernel.AccessPlatformMmio32(owner,copy).IsSuccess);Assert.False(kernel.ResolvePlatformMmio32(owner,copy).IsSuccess);Assert.Equal(1,spy.Writes);
        var foreignDevice=Device(kernel,foreign,new(30320),"device/foreign-registers");
        var foreignCap=kernel.MintCapability(new(30320),foreign,ResourceKind.MmioRegion,CapabilityResourceIds.MmioRegion("device/foreign-registers","bank",32),CapabilityRights.Map|CapabilityRights.Read|CapabilityRights.Write).Value!.CapabilityId;
        var foreignMap=kernel.BindPlatformMmioBacking(foreign,foreignDevice,foreignCap,0,32,PlatformMmioAccess.Read|PlatformMmioAccess.Write,peer).Value;
        Assert.True(kernel.AccessPlatformMmio32(foreign,new(foreignMap,0,true,99)).IsSuccess);
        if(loss)
        {
            spy.LoseClose=true;Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
            Assert.NotEqual(RegionUseState.Released,kernel.Regions.SnapshotUses().Single(u=>u.Region==buffer.Handle).State);
            Assert.Equal(64UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);spy.LoseClose=false;
        }
        var closed=kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(2,spy.Maps);Assert.Equal(2,spy.Writes);Assert.Equal(1,spy.Reads);Assert.Equal(1,spy.Closes);
        Assert.Equal(RegionUseState.Released,kernel.Regions.SnapshotUses().Single(u=>u.Region==buffer.Handle).State);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==peer.Handle).State);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage,u=>Assert.Equal(0UL,u.Used));
        Assert.Equal(32UL,kernel.QueryBudget(foreignBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        var foreignRead=kernel.AccessPlatformMmio32(foreign,new(foreignMap,0,false));Assert.True(foreignRead.IsSuccess,foreignRead.Message);Assert.Equal(99U,foreignRead.Value);
        Assert.Equal(1,spy.Inner.ActiveMmioLeaseCount);
    }
    private static PlatformDeviceLease Device(RuntimeKernel kernel,ProcessHandle owner,DomainId realm,string name)
    {
        var root=kernel.BindPlatformDomain(owner).Value;
        var cap=kernel.MintCapability(realm,owner,ResourceKind.Device,name,CapabilityRights.Configure|CapabilityRights.Read|CapabilityRights.Write).Value!.CapabilityId;
        return kernel.BindPlatformDevice(owner,root,cap,PlatformDeviceRights.Configure|PlatformDeviceRights.Read|PlatformDeviceRights.Write).Value;
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RegisterReentryAndOverlapCannotReleaseOrRepeatPossibleEffect(bool overlap)
    {
        var proxy=DispatchProxy.Create<IRuntime,Spy>();var spy=(Spy)(object)proxy;
        var provider=new HybridCpuPlatformAuthorityProvider(proxy);var kernel=new RuntimeKernel(provider);
        var owner=TestFixtures.Create(kernel,3041,30410).Handle;var buffer=kernel.AllocateBuffer<byte>(owner,64).Value!;
        var device=Device(kernel,owner,new(30410),"device/reentry");
        var cap=kernel.MintCapability(new(30410),owner,ResourceKind.MmioRegion,CapabilityResourceIds.MmioRegion("device/reentry","bank",64),CapabilityRights.Map|CapabilityRights.Read|CapabilityRights.Write).Value!.CapabilityId;
        var mapped=kernel.BindPlatformMmioBacking(owner,device,cap,0,64,PlatformMmioAccess.Read|PlatformMmioAccess.Write,buffer);Assert.True(mapped.IsSuccess,mapped.Message);var map=mapped.Value;
        var original=new PlatformMmio32Invocation(map,4,true,42);var use=Assert.Single(kernel.Regions.SnapshotUses());
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        spy.BeforeAccess=()=>
        {
            const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
            Assert.False(Monitor.IsEntered(typeof(RuntimeKernel).GetField("_platformMemoryUseGate",flags)!.GetValue(kernel)!));
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate",flags)!.GetValue(kernel.PlatformAuthority)!));
            Assert.False(Monitor.IsEntered(typeof(HybridCpuPlatformAuthorityProvider).GetField("_childCreateCommitGate",flags)!.GetValue(provider)!));
            if(overlap){entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(10)));}
            else
            {
                Assert.False(kernel.AccessPlatformMmio32(owner,original).IsSuccess);
                Assert.False(kernel.RevokePlatformMmio(owner,map).IsSuccess);
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            }
        };
        KernelResult<uint> result;
        if(overlap)
        {
            var pending=Task.Run(()=>kernel.AccessPlatformMmio32(owner,original));
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                Assert.False(kernel.AccessPlatformMmio32(owner,new(map,0,true,11)).IsSuccess);
                Assert.False(kernel.RevokePlatformMmio(owner,map).IsSuccess);
                Assert.False(kernel.ReleaseRegionUse(owner,use.Handle).IsSuccess);
            }
            finally{release.Set();}
            result=await pending;
        }
        else result=kernel.AccessPlatformMmio32(owner,original);
        Assert.Equal(overlap,result.IsSuccess);Assert.Equal(1,spy.Writes);
        spy.BeforeAccess=null;
        var known=kernel.ResolvePlatformMmio32(owner,original);Assert.True(known.IsSuccess,known.Message);Assert.Equal(42U,known.Value);
        var closed=kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(1,spy.Closes);Assert.Equal(RegionUseState.Released,Assert.Single(kernel.Regions.SnapshotUses()).State);
    }
    [Theory]
    [InlineData("use-before")] [InlineData("cap-after")] [InlineData("malformed")]
    public void RegisterSourceAndRawChangesNeverRepeatOrReleaseBackingEarly(string fault)
    {
        var proxy=DispatchProxy.Create<IRuntime,Spy>();var spy=(Spy)(object)proxy;var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(proxy));
        var owner=TestFixtures.Create(kernel,3051,30510).Handle;var buffer=kernel.AllocateBuffer<byte>(owner,64).Value!;
        var device=Device(kernel,owner,new(30510),"device/fault");
        var cap=kernel.MintCapability(new(30510),owner,ResourceKind.MmioRegion,CapabilityResourceIds.MmioRegion("device/fault","bank",64),CapabilityRights.Map|CapabilityRights.Read|CapabilityRights.Write).Value!.CapabilityId;
        var mapped=kernel.BindPlatformMmioBacking(owner,device,cap,0,64,PlatformMmioAccess.Read|PlatformMmioAccess.Write,buffer);Assert.True(mapped.IsSuccess,mapped.Message);var map=mapped.Value;
        var use=Assert.Single(kernel.Regions.SnapshotUses());var original=new PlatformMmio32Invocation(map,0,true,42);
        if(fault=="use-before")Assert.True(kernel.InvalidateRegionUse(owner,use.Handle).IsSuccess);
        if(fault=="cap-after")spy.AfterAccess=()=>Assert.True(kernel.CapabilityAuthority.Revoke(cap).IsSuccess);
        spy.Malformed=fault=="malformed";
        Assert.False(kernel.AccessPlatformMmio32(owner,original).IsSuccess);
        Assert.False(kernel.AccessPlatformMmio32(owner,original).IsSuccess);
        Assert.Equal(fault=="use-before"?0:1,spy.Writes);
        Assert.NotEqual(RegionUseState.Released,Assert.Single(kernel.Regions.SnapshotUses()).State);
        spy.AfterAccess=null;spy.Malformed=false;
        var known=kernel.ResolvePlatformMmio32(owner,original);Assert.Equal(fault!="use-before",known.IsSuccess);
        if(known.IsSuccess)Assert.Equal(42U,known.Value);
        var closed=kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(RegionUseState.Released,Assert.Single(kernel.Regions.SnapshotUses()).State);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
    }
    [Fact]
    public void MissingNativeRegisterHistoryCannotProveCloseOrReleaseDependentRegion()
    {
        var proxy=DispatchProxy.Create<IRuntime,Spy>();var spy=(Spy)(object)proxy;var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(proxy));
        var owner=TestFixtures.Create(kernel,3061,30610).Handle;var buffer=kernel.AllocateBuffer<byte>(owner,64).Value!;
        var device=Device(kernel,owner,new(30610),"device/history");
        var cap=kernel.MintCapability(new(30610),owner,ResourceKind.MmioRegion,CapabilityResourceIds.MmioRegion("device/history","bank",64),CapabilityRights.Map|CapabilityRights.Read|CapabilityRights.Write).Value!.CapabilityId;
        var mapped=kernel.BindPlatformMmioBacking(owner,device,cap,0,64,PlatformMmioAccess.Read|PlatformMmioAccess.Write,buffer);Assert.True(mapped.IsSuccess,mapped.Message);
        spy.AfterAccess=()=>
        {
            const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
            var registry=typeof(NeutralDomainRuntimeFacade).GetField("_dependencies",flags)!.GetValue(spy.Inner)!;
            var maps=(global::System.Collections.IDictionary)registry.GetType().GetField("Mmio",flags)!.GetValue(registry)!;
            var source=Assert.Single(maps.Values.Cast<object>());
            ((global::System.Collections.IList)source.GetType().GetProperty("Accesses")!.GetValue(source)!).Clear();
        };
        var original=new PlatformMmio32Invocation(mapped.Value,0,true,42);
        Assert.False(kernel.AccessPlatformMmio32(owner,original).IsSuccess);spy.AfterAccess=null;
        Assert.False(kernel.ResolvePlatformMmio32(owner,original).IsSuccess);
        Assert.False(kernel.AccessPlatformMmio32(owner,original).IsSuccess);Assert.Equal(1,spy.Writes);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(1,spy.Inner.ActiveMmioLeaseCount);
        Assert.NotEqual(RegionUseState.Released,Assert.Single(kernel.Regions.SnapshotUses()).State);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
    }
    [Theory]
    [InlineData("foreign")] [InlineData("mapped")] [InlineData("capacity")]
    public void MmioBackingAcquisitionPreservesRegionGuardsAndConsumesOnlyOriginalNoEffect(string fault)
    {
        var proxy=DispatchProxy.Create<IRuntime,Spy>();var spy=(Spy)(object)proxy;var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(proxy));
        var owner=TestFixtures.Create(kernel,3071,30710).Handle;var foreign=TestFixtures.Create(kernel,3072,30720).Handle;
        var buffer=kernel.AllocateBuffer<byte>(fault=="foreign"?foreign:owner,64).Value!;
        var device=Device(kernel,owner,new(30710),"device/acquire");
        var cap=kernel.MintCapability(new(30710),owner,ResourceKind.MmioRegion,CapabilityResourceIds.MmioRegion("device/acquire","bank",64),CapabilityRights.Map|CapabilityRights.Read|CapabilityRights.Write).Value!.CapabilityId;
        if(fault=="mapped")
        {
            var memory=kernel.MintCapability(new(30710),owner,ResourceKind.MemoryRegion,CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),CapabilityRights.Map|CapabilityRights.Read).Value!.CapabilityId;
            var mapped=kernel.MapPlatformOwnedRegion(owner,device.DomainBinding,memory,buffer.Handle,PlatformMemoryAccess.Read);Assert.True(mapped.IsSuccess,mapped.Message);
        }
        if(fault=="capacity")typeof(NeutralDomainRuntimeFacade).GetField("_nextResource",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(spy.Inner,ulong.MaxValue);
        var refused=kernel.BindPlatformMmioBacking(owner,device,cap,0,64,PlatformMmioAccess.Read|PlatformMmioAccess.Write,buffer);
        Assert.False(refused.IsSuccess);Assert.Equal(fault=="capacity"?1:0,spy.Maps);
        if(fault=="capacity")Assert.NotEqual(RegionUseState.Released,Assert.Single(kernel.Regions.SnapshotUses()).State);
        else Assert.Empty(kernel.Regions.SnapshotUses());
        var ended=kernel.TerminateProcess(owner);Assert.True(ended.IsSuccess,ended.Message);Assert.Equal(0,spy.Closes);
        Assert.Equal(fault=="foreign"?RegionState.Owned:RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        if(fault=="capacity")Assert.Equal(RegionUseState.Released,Assert.Single(kernel.Regions.SnapshotUses()).State);
    }
    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public void ImportedRegisterBackingRetainsOriginalReservationThroughRouteLossAndCleanup(bool foreignRoute,bool afterEffect)
    {
        var proxy=DispatchProxy.Create<IRuntime,Spy>();var spy=(Spy)(object)proxy;var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(proxy));
        var owner=TestFixtures.Create(kernel,3081,30810).Handle;var foreign=TestFixtures.Create(kernel,3082,30820).Handle;var admin=TestFixtures.Create(kernel,3083,30830).Handle;
        var budgetCap=kernel.MintCapability(new(30830),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(admin,budgetCap,owner,"route-mmio",[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]).Value!.ProcessBudget;
        var buffer=kernel.AllocateBuffer<byte>(owner,64).Value!;var peer=kernel.AllocateBuffer<byte>(foreign,32).Value!;
        PlatformMmioLease Map(ProcessHandle process,DomainId domain,string id,SingPlus.Sip.OwnedBuffer<byte> bytes)
        {
            var device=Device(kernel,process,domain,id);
            var cap=kernel.MintCapability(domain,process,ResourceKind.MmioRegion,CapabilityResourceIds.MmioRegion(id,"bank",bytes.Length),CapabilityRights.Map|CapabilityRights.Read|CapabilityRights.Write).Value!.CapabilityId;
            return kernel.BindPlatformMmioBacking(process,device,cap,0,bytes.Length,PlatformMmioAccess.Read|PlatformMmioAccess.Write,bytes).Value;
        }
        var map=Map(owner,new(30810),"device/route",buffer);var foreignMap=Map(foreign,new(30820),"device/route-peer",peer);
        Assert.True(kernel.AccessPlatformMmio32(foreign,new(foreignMap,0,true,99)).IsSuccess);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        var registry=(global::System.Collections.IDictionary)kernel.PlatformAuthority.GetType().GetField("_originalMmioOwners",flags)!.GetValue(kernel.PlatformAuthority)!;
        object Property(object value,string name)=>value.GetType().GetProperty(name,flags)!.GetValue(value)!;
        var payload=Property(Property(registry[map.LeaseId]!,"OriginalMap"),"Payload");
        var other=Property(Property(registry[foreignMap.LeaseId]!,"OriginalMap"),"Payload");
        var lease=Property(payload,"Reservation");var foreignLease=Property(other,"Reservation");
        var storage=lease.GetType().GetField("_storage",flags)!.GetValue(lease)!;
        var route=storage.GetType().GetField("_runtimeReservation",flags)!;var original=route.GetValue(storage);
        var foreignLifetime=foreignLease.GetType().GetField("_lifetime",flags)!.GetValue(foreignLease);
        var writes=spy.Writes;var reads=spy.Reads;
        if(afterEffect){spy.LoseAccess=true;spy.AfterAccess=()=>route.SetValue(storage,foreignRoute?foreignLifetime:null);}
        else route.SetValue(storage,foreignRoute?foreignLifetime:null);
        var invocation=new PlatformMmio32Invocation(map,0,true,42);
        Assert.False(kernel.AccessPlatformMmio32(owner,invocation).IsSuccess);spy.AfterAccess=null;
        Assert.Equal(writes+(afterEffect?1:0),spy.Writes);
        Assert.False(kernel.AccessPlatformMmio32(owner,new(map,4,true,77)).IsSuccess);
        Assert.False(kernel.AccessPlatformMmio32(owner,new(map,0,false)).IsSuccess);Assert.Equal(reads,spy.Reads);
        spy.LoseAccess=false;
        if(afterEffect){var known=kernel.ResolvePlatformMmio32(owner,invocation);Assert.True(known.IsSuccess,known.Message);Assert.Equal(42U,known.Value);}
        KernelResult ended=default;var escaped=Record.Exception(()=>ended=kernel.TerminateProcess(owner));
        Assert.Null(escaped);Assert.False(ended.IsSuccess);Assert.Equal(1,spy.Closes);
        Assert.Equal(64UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        route.SetValue(storage,original);
        var retried=kernel.TerminateProcess(owner);Assert.True(retried.IsSuccess,retried.Message);
        Assert.Equal(1,spy.Closes);Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        var foreignRead=kernel.AccessPlatformMmio32(foreign,new(foreignMap,0,false));Assert.True(foreignRead.IsSuccess,foreignRead.Message);Assert.Equal(99U,foreignRead.Value);
        Assert.True((bool)Property(foreignLease,"IsActive"));Assert.Throws<InvalidOperationException>(()=>peer.Span.ToArray());
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==peer.Handle).State);
    }}
