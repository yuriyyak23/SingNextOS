using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Tests.Platform;
public sealed partial class PlatformDsc1ComputeTests
{
    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public void OriginalDsc1TwoViewCleanupRetainsStagingChargeAndNeverRepeatsLocalPublication(bool sourceFault,bool discard)
    {
        Scenario Build(IPlatformAuthorityProvider provider,ulong id,ulong domain,out BudgetAccountHandle account)
        {
            BudgetAccountHandle captured=default;
            Scenario result =CreateScenario(provider,id,domain,32,(kernel,subject)=>
            {
                ProcessHandle admin =TestFixtures.Create(kernel,id+1,domain+1).Handle;
                CapabilityId cap =kernel.MintCapability(new(domain+1),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
                captured=kernel.AdmitProcessBudget(admin,cap,subject,"dsc1-view-retry",[new(ServiceBudgetDimension.OwnedMemoryBytes,128)]).Value!.ProcessBudget;
            },originalSlices:true);
            account=captured;return result;
        }
        IOriginalDsc1Runtime proxy =DispatchProxy.Create<IOriginalDsc1Runtime,Dsc1NativeSpy>(); Dsc1NativeSpy spy =(Dsc1NativeSpy)(object)proxy;spy.Native=new();
        Scenario x =Build(new HybridCpuPlatformAuthorityProvider(proxy),2591,25910,out BudgetAccountHandle budget);
        Scenario foreign =Build(new HybridCpuPlatformAuthorityProvider(),2595,25950,out BudgetAccountHandle foreignBudget);
        x.Source.Span.Fill(0x6B);foreign.Source.Span.Fill(0x7D);
        KernelResult<PlatformDsc1CopySubmission> submission =SubmitWhole(x);Assert.True(submission.IsSuccess,submission.Message);Assert.True(SubmitWhole(foreign).IsSuccess);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        IDictionary Inventory(RuntimeKernel kernel)=>(IDictionary)kernel.GetType().GetField("_dsc1PayloadOperations",flags)!.GetValue(kernel)!;
        object payload =Assert.Single(Inventory(x.Kernel).Values.Cast<object>()); object other =Assert.Single(Inventory(foreign.Kernel).Values.Cast<object>());
        object lease =payload.GetType().GetProperty(sourceFault?"SourceLease":"DestinationLease")!.GetValue(payload)!;
        object foreignLease =other.GetType().GetProperty("SourceLease")!.GetValue(other)!;
        object storage =lease.GetType().GetField("_storage",flags)!.GetValue(lease)!;
        FieldInfo route =storage.GetType().GetField("_runtimeReservation",flags)!; object? original =route.GetValue(storage);
        route.SetValue(storage,foreignLease.GetType().GetField("_lifetime",flags)!.GetValue(foreignLease));
        KernelResult<PlatformDsc1CopyReceipt> observed=default;KernelResult ended=default;
        Exception escaped =Record.Exception(()=>{if(discard)ended=x.Kernel.TerminateProcess(x.Subject);else observed=x.Kernel.ObservePlatformDsc1Copy(x.Subject,submission.Value);});
        Assert.Null(escaped);Assert.False(discard?ended.IsSuccess:observed.IsSuccess);
        Assert.Equal(128UL,x.Kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Single(Inventory(x.Kernel));Assert.Equal(1,spy.Copies);
        if(!discard && sourceFault)
        {
            Assert.All(x.Destination.Span.ToArray(),b=>Assert.Equal((byte)0x6B,b));
            x.Destination.Span.Fill(0x17); // Past publication cannot be repeated by cleanup retry.
            _=x.Kernel.RevokeCapability(x.ComputeCapability.CapabilityId); // Local revoke commits even while exact view cleanup is blocked.
            Assert.False(x.Kernel.CapabilityAuthority.Validate(x.ComputeCapability.CapabilityId,new(25910),x.Subject.Generation,CapabilityRights.Execute).IsSuccess);
        }
        route.SetValue(storage,original);
        if(discard)Assert.True(x.Kernel.TerminateProcess(x.Subject).IsSuccess);
        else
        {
            KernelResult<PlatformDsc1CopyReceipt> retried =x.Kernel.ObservePlatformDsc1Copy(x.Subject,submission.Value);Assert.True(retried.IsSuccess,retried.Message);
            Assert.Equal(sourceFault,retried.Value.OutputPublished);
            Assert.All(x.Destination.Span.ToArray(),b=>Assert.Equal(sourceFault?(byte)0x17:(byte)0,b));
            Assert.Equal(64UL,x.Kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.True(x.Kernel.TerminateProcess(x.Subject).IsSuccess);
        }
        Assert.Equal(1,spy.Copies);Assert.Empty(Inventory(x.Kernel));
        Assert.Equal(0UL,x.Kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.All(x.Kernel.Regions.Snapshot().Where(r=>r.Handle==x.Source.Handle||r.Handle==x.Destination.Handle),r=>Assert.Equal(RegionState.Released,r.State));
        Assert.Equal(128UL,foreign.Kernel.QueryBudget(foreignBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True((bool)foreignLease.GetType().GetProperty("IsActive",flags)!.GetValue(foreignLease)!);
        Assert.Throws<InvalidOperationException>(()=>foreign.Destination.Span.ToArray());Assert.Single(Inventory(foreign.Kernel));
    }
}
