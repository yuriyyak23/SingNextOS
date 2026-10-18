using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed partial class PlatformDsc1ComputeTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void SameOwnerDsc1ChargeSubstitutionCannotSettleIndependentStaging(int fault)
    {
        var runtime=DispatchProxy.Create<IOriginalDsc1Runtime,Dsc1NativeSpy>();var spy=(Dsc1NativeSpy)(object)runtime;spy.Native=new();
        BudgetAccountHandle budget=default;
        var x=CreateScenario(new HybridCpuPlatformAuthorityProvider(runtime),2591,25910,32,(kernel,owner)=>
        {
            var admin=TestFixtures.Create(kernel,2592,25911).Handle;
            var cap=kernel.MintCapability(new(25911),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
            budget=kernel.AdmitProcessBudget(admin,cap,owner,"two-dsc1-charges",[new(ServiceBudgetDimension.OwnedMemoryBytes,256)]).Value!.ProcessBudget;
        },originalSlices:true);
        var source=x.Kernel.AllocateBuffer<byte>(x.Subject,32).Value!;
        var destination=x.Kernel.AllocateBuffer<byte>(x.Subject,32).Value!;
        PlatformRegionMapping Map(RegionHandle region,CapabilityRights rights,PlatformMemoryAccess access)
        {
            var cap=x.Kernel.MintCapability(new(25910),x.Subject,ResourceKind.MemoryRegion,CapabilityResourceIds.MemoryRegion(region.RegionId),CapabilityRights.Map|rights).Value!.CapabilityId;
            return x.Kernel.MapPlatformOwnedRegionSlice(x.Subject,x.Binding,cap,region,0,32,access).Value!.Mapping;
        }
        var peer=x with {Source=source,Destination=destination,SourceMapping=Map(source.Handle,CapabilityRights.Read,PlatformMemoryAccess.Read),DestinationMapping=Map(destination.Handle,CapabilityRights.Write,PlatformMemoryAccess.Write)};
        x.Source.Span.Fill(0x6B);peer.Source.Span.Fill(0x7D);
        var submitted=SubmitWhole(x);Assert.True(submitted.IsSuccess,submitted.Message);
        var independent=SubmitWhole(peer);Assert.True(independent.IsSuccess,independent.Message);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        var inventory=(IDictionary)x.Kernel.GetType().GetField("_dsc1PayloadOperations",flags)!.GetValue(x.Kernel)!;
        var payload=inventory[submitted.Value.SubmissionId]!;var other=inventory[independent.Value.SubmissionId]!;
        var charge=payload.GetType().GetProperty("StagedOutputCharge")!;var original=charge.GetValue(payload);
        var consumed=payload.GetType().GetProperty("StagedOutputChargeConsumed")!;
        var budgetRoutes=(IDictionary)x.Kernel.Budgets.GetType().GetField("_reservations",flags)!.GetValue(x.Kernel.Budgets)!;
        var handle=(BudgetReservationHandle)original!;var originalCharge=budgetRoutes[handle.ReservationId]!;
        if(fault==1)consumed.SetValue(payload,true);
        else if(fault==2)budgetRoutes.Remove(handle.ReservationId);
        else charge.SetValue(payload,charge.GetValue(other));
        ulong Used()=>x.Kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        var refused=x.Kernel.ObservePlatformDsc1Copy(x.Subject,submitted.Value);
        Assert.False(refused.IsSuccess);Assert.Equal(256UL,Used());Assert.Equal(2,inventory.Count);Assert.Equal(2,spy.Copies);
        var peerLease=other.GetType().GetProperty("SourceLease")!.GetValue(other)!;
        Assert.True((bool)peerLease.GetType().GetProperty("IsActive",flags)!.GetValue(peerLease)!);
        Assert.True(x.Kernel.ObservePlatformDsc1Copy(x.Subject,independent.Value).IsSuccess);
        Assert.Equal(192UL,Used());Assert.All(peer.Destination.Span.ToArray(),b=>Assert.Equal((byte)0x7D,b));
        charge.SetValue(payload,original);consumed.SetValue(payload,false);
        if(fault==2)budgetRoutes[handle.ReservationId]=originalCharge;
        Assert.True(x.Kernel.ObservePlatformDsc1Copy(x.Subject,submitted.Value).IsSuccess);Assert.Equal(128UL,Used());
        Assert.True(x.Kernel.TerminateProcess(x.Subject).IsSuccess);Assert.Equal(0UL,Used());Assert.Equal(2,spy.Copies);Assert.Empty(inventory);
        Assert.All(x.Kernel.Regions.Snapshot(),r=>Assert.Equal(RegionState.Released,r.State));
    }
}
