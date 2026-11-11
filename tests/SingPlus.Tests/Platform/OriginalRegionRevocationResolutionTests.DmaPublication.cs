using System.Collections;
using SingPlus.Contracts;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Tests.Platform;

public sealed partial class OriginalRegionRevocationResolutionTests
{
    [Fact]
    public void ActualDmaSourceRegionClosureRetainsOriginalChargeThroughForeignBudgetRoute()
    {
        (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) x =CopyKernel(crossOwner:true); (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) foreign =CopyKernel(crossOwner:true);
        PlatformDmaPrepareEvidence sp =x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value!;
        PlatformDmaPrepareEvidence dp =x.Kernel.PreparePlatformDmaForDevice(x.DestinationOwner,x.Destination).Value!;
        const global::System.Reflection.BindingFlags flags=global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic;
        global::System.Collections.IDictionary Routes(RuntimeKernel kernel)=>(global::System.Collections.IDictionary)kernel.GetType().GetField("_regionBudgetReservations",flags)!.GetValue(kernel)!;
        IDictionary routes =Routes(x.Kernel); object original =routes[x.Input.Handle]!;
        object foreignRoute =Routes(foreign.Kernel)[foreign.Output.Handle]!;
        x.Kernel.BeforeRegionReleaseQualificationHook=()=>
        {x.Kernel.BeforeRegionReleaseQualificationHook=null;routes[x.Input.Handle]=foreignRoute;};
        KernelResult<V6DataMotionExecution<byte>> moved =x.Kernel.ExecuteV6ProviderDmaDataMotion(x.Owner,x.DestinationOwner,x.Input,x.Output,x.Plan,
            x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp,()=>3,()=>5);
        Assert.True(moved.IsSuccess,moved.Message);Assert.False(x.Input.IsValid);
        Assert.Equal(RegionState.Released,x.Kernel.Regions.Snapshot().Single(r=>r.Handle==x.Input.Handle).State);
        Assert.Equal(64UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(64UL,foreign.Kernel.QueryBudget(foreign.Budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(64,foreign.Input.Span.Length);Assert.Equal(2,foreign.Native.ActiveDmaGrantCount);
        routes[x.Input.Handle]=original;
        Assert.True(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True(x.Kernel.TerminateProcess(x.DestinationOwner).IsSuccess);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(64,foreign.Input.Span.Length);
    }
    [Fact]
    public void ReentrantSourceConsumptionEndsClosedDestinationCustodyWithoutPublicationOrRepeat()
    {
        (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) x =CopyKernel(crossOwner:true);
        PlatformDmaPrepareEvidence sp =x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value!;
        PlatformDmaPrepareEvidence dp =x.Kernel.PreparePlatformDmaForDevice(x.DestinationOwner,x.Destination).Value!;
        int copies =0;x.Spy.AfterOperation=name=>{if(name=="CopyDma")copies++;};
        x.Kernel.BeforeRegionReleaseQualificationHook=()=>
        {x.Kernel.BeforeRegionReleaseQualificationHook=null;Assert.True(x.Kernel.ReleaseRegion(x.Owner,x.Input).IsSuccess);};
        KernelResult<V6DataMotionExecution<byte>> moved =x.Kernel.ExecuteV6ProviderDmaDataMotion(x.Owner,x.DestinationOwner,x.Input,x.Output,x.Plan,
            x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp,()=>3,()=>5);
        Assert.False(moved.IsSuccess);Assert.Equal(1,copies);Assert.False(x.Input.IsValid);
        Assert.Equal(64,x.Output.Span.Length);
        IDictionary records =(global::System.Collections.IDictionary)x.Kernel.GetType().GetField("_dmaCopyPayloads",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.GetValue(x.Kernel)!;
        Assert.Empty(records);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(64UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True(x.Kernel.TerminateProcess(x.Owner).IsSuccess);Assert.True(x.Kernel.TerminateProcess(x.DestinationOwner).IsSuccess);
    }
    [Fact]
    public void ActualDmaInterruptedSourceReclaimRetriesSameOriginalWithoutEffectRepeat()
    {
        (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) x =CopyKernel(crossOwner:true);
        PlatformDmaPrepareEvidence sp =x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value!;
        PlatformDmaPrepareEvidence dp =x.Kernel.PreparePlatformDmaForDevice(x.DestinationOwner,x.Destination).Value!;
        int copies =0;x.Spy.AfterOperation=name=>{if(name=="CopyDma")copies++;};
        KernelResult<V6DataMotionExecution<byte>> Move()=>x.Kernel.ExecuteV6ProviderDmaDataMotion(x.Owner,x.DestinationOwner,x.Input,x.Output,x.Plan,
            x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp,()=>3,()=>5);
        x.Kernel.BeforeRegionReleaseQualificationHook=()=>
        {x.Kernel.BeforeRegionReleaseQualificationHook=null;throw new IOException("Source reclaim interrupted");};
        Assert.Throws<IOException>(()=>Move());
        Assert.Equal(1,copies);Assert.Equal(0,x.Native.ActiveDmaGrantCount);Assert.Equal(0,x.Native.ActiveOwnedRegionMappingCount);
        Assert.Throws<InvalidOperationException>(()=>{_=x.Output.Span.Length;});
        Assert.Equal(64UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(64UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        KernelResult<V6DataMotionExecution<byte>> retried =Move();Assert.True(retried.IsSuccess,retried.Message);Assert.Equal(1,copies);
        Assert.All(x.Output.Span.ToArray(),b=>Assert.Equal((byte)0x5A,b));
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True(x.Kernel.TerminateProcess(x.Owner).IsSuccess);Assert.True(x.Kernel.TerminateProcess(x.DestinationOwner).IsSuccess);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualDmaDestinationRemainsReservedDuringSourceReclaimUntilFinalPublication(bool stopDestination)
    {
        (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) x =CopyKernel(crossOwner:true);
        PlatformDmaPrepareEvidence sp =x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value!;
        PlatformDmaPrepareEvidence dp =x.Kernel.PreparePlatformDmaForDevice(x.DestinationOwner,x.Destination).Value!;
        int copies =0; x.Spy.AfterOperation=name=>{if(name=="CopyDma")copies++;};
        bool callback =false;
        x.Kernel.BeforeRegionReleaseQualificationHook=()=>
        {
            x.Kernel.BeforeRegionReleaseQualificationHook=null; callback=true;
            Assert.Equal(0,x.Native.ActiveDmaGrantCount);
            Assert.Equal(0,x.Native.ActiveOwnedRegionMappingCount);
            Assert.Throws<InvalidOperationException>(()=>{_=x.Output.Span.Length;});
            Assert.Equal(64UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            if(stopDestination)
            {
                KernelResult stopped =x.Kernel.TerminateProcess(x.DestinationOwner);
                Assert.True(stopped.IsSuccess,stopped.Message);
            }
        };
        KernelResult<V6DataMotionExecution<byte>> moved =x.Kernel.ExecuteV6ProviderDmaDataMotion(x.Owner,x.DestinationOwner,x.Input,x.Output,x.Plan,
            x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp,()=>3,()=>5);
        Assert.Equal(!stopDestination,moved.IsSuccess); Assert.True(callback); Assert.Equal(1,copies);
        if(stopDestination)Assert.Throws<InvalidOperationException>(()=>{_=x.Output.Span.Length;});
        else Assert.All(x.Output.Span.ToArray(),b=>Assert.Equal((byte)0x5A,b));
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(stopDestination ? 0UL : 64UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        if(!stopDestination)Assert.True(x.Kernel.TerminateProcess(x.DestinationOwner).IsSuccess);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }
}
