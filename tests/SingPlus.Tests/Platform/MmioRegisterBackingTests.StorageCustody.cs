using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed partial class MmioRegisterBackingTests
{
    [Fact]
    public void NativeCloseThenDirectReleasePreservesForeignReservationLifetime()
    {
        IRuntime proxy =DispatchProxy.Create<IRuntime,Spy>(); Spy spy =(Spy)(object)proxy;
        RuntimeKernel kernel =new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(proxy));
        ProcessHandle owner =TestFixtures.Create(kernel,3081,30810).Handle;
        ProcessHandle foreign =TestFixtures.Create(kernel,3082,30820).Handle;
        ProcessHandle admin =TestFixtures.Create(kernel,3083,30830).Handle;
        CapabilityId cap =kernel.MintCapability(new(30830),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(admin,cap,owner,"storage-owner",[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]).Value!.ProcessBudget;
        BudgetAccountHandle peerBudget =kernel.AdmitProcessBudget(admin,cap,foreign,"storage-peer",[new(ServiceBudgetDimension.OwnedMemoryBytes,32)]).Value!.ProcessBudget;
        OwnedBuffer<byte> buffer =kernel.AllocateBuffer<byte>(owner,64).Value!; OwnedBuffer<byte> peer =kernel.AllocateBuffer<byte>(foreign,32).Value!;
        PlatformMmioLease Map(ProcessHandle process,DomainId domain,string id,SingPlus.Sip.OwnedBuffer<byte> bytes)
        {
            PlatformDeviceLease device =Device(kernel,process,domain,id);
            CapabilityId mmio =kernel.MintCapability(domain,process,ResourceKind.MmioRegion,CapabilityResourceIds.MmioRegion(id,"bank",bytes.Length),CapabilityRights.Map|CapabilityRights.Read|CapabilityRights.Write).Value!.CapabilityId;
            return kernel.BindPlatformMmioBacking(process,device,mmio,0,bytes.Length,PlatformMmioAccess.Read|PlatformMmioAccess.Write,bytes).Value;
        }
        PlatformMmioLease map =Map(owner,new(30810),"device/storage",buffer);
        PlatformMmioLease peerMap =Map(foreign,new(30820),"device/storage-peer",peer);
        Assert.True(kernel.AccessPlatformMmio32(owner,new(map,0,true,42)).IsSuccess);
        Assert.True(kernel.AccessPlatformMmio32(foreign,new(peerMap,0,true,99)).IsSuccess);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        object Property(object value,string name)=>value.GetType().GetProperty(name,flags)!.GetValue(value)!;
        IDictionary registry =(global::System.Collections.IDictionary)kernel.PlatformAuthority.GetType().GetField("_originalMmioOwners",flags)!.GetValue(kernel.PlatformAuthority)!;
        object Lease(PlatformMmioLease value)=>Property(Property(Property(registry[value.LeaseId]!,"OriginalMap"),"Payload"),"Reservation");
        object lease =Lease(map); object peerLease =Lease(peerMap);
        object storage =lease.GetType().GetField("_storage",flags)!.GetValue(lease)!;
        FieldInfo anchor =storage.GetType().GetField("_originalRuntimeReservation",flags)!;
        anchor.SetValue(storage,peerLease.GetType().GetField("_lifetime",flags)!.GetValue(peerLease));
        Assert.False(kernel.RevokePlatformMmio(owner,map).IsSuccess);
        Assert.Equal(1,spy.Closes);Assert.Equal(2,spy.Maps);Assert.Equal(2,spy.Writes);
        Assert.Equal(64UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        KernelResult released =kernel.ReleaseRegion(owner,buffer);Assert.True(released.IsSuccess,released.Message);
        Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        KernelResult<uint> read =kernel.AccessPlatformMmio32(foreign,new(peerMap,0,false));Assert.True(read.IsSuccess,read.Message);Assert.Equal(99U,read.Value);
        Assert.Equal(1,spy.Reads);Assert.True((bool)Property(peerLease,"IsActive"));
        Assert.Equal(32UL,kernel.QueryBudget(peerBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(1,spy.Closes);
        Assert.True(kernel.TerminateProcess(foreign).IsSuccess);Assert.Equal(2,spy.Closes);
        Assert.Equal(0UL,kernel.QueryBudget(peerBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }
}
