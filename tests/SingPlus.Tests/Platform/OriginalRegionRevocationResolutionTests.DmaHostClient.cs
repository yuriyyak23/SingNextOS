using System.Collections;
using SingPlus.Contracts;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Tests.Platform;

public sealed partial class OriginalRegionRevocationResolutionTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void OriginalDmaCancellationSeparatesNoCopyCompletionAndSourceConsumption(int point)
    {
        (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) x =CopyKernel(true); V6ManagedLocalityCostProvider costs =new V6ManagedLocalityCostProvider(); long now =DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.True(costs.Publish(new(1,"source",3,now,now+60000,64,1,0,0)).IsSuccess);
        Assert.True(costs.Publish(new(1,"destination",5,now,now+60000,64,1,0,0)).IsSuccess);
        PlatformDmaPrepareEvidence sp =x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value;
        PlatformDmaPrepareEvidence dp =x.Kernel.PreparePlatformDmaForDevice(x.DestinationOwner,x.Destination).Value;
        ProviderDmaDataMotionRequest<byte> original =new ProviderDmaDataMotionRequest<byte>(x.Owner,x.DestinationOwner,x.Input,x.Output,x.Plan,x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp);
        using CancellationTokenSource cancellation =new CancellationTokenSource(); int copies =0;
        if(point==0)cancellation.Cancel();
        x.Spy.AfterOperation=name=>{if(name=="CopyDma"){copies++;if(point==1)cancellation.Cancel();}};
        if(point==2)x.Kernel.BeforeRegionReleaseQualificationHook=()=>cancellation.Cancel();
        KernelResult<ProviderDmaDataMotionResult<byte>> moved =x.Kernel.MoveProviderDmaBuffers(original,costs,cancellation.Token);
        Assert.Equal(KernelError.CancellationPending,moved.Error);Assert.Equal(point==0?0:1,copies);
        Assert.False(x.Kernel.MoveProviderDmaBuffers(original,costs).IsSuccess);
        Assert.False(x.Kernel.ReadProviderDmaDataMotionResult(original).IsSuccess);
        Assert.Equal(point!=2,x.Input.IsValid);
        Assert.Equal(64UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        x.Kernel.BeforeRegionReleaseQualificationHook=null;x.Spy.AfterOperation=null;
        Assert.True(x.Kernel.CleanupProviderDmaDataMotion(original).IsSuccess);
        Assert.True(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.True(x.Kernel.TerminateProcess(x.DestinationOwner).IsSuccess);
        Assert.All(x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage,u=>Assert.Equal(0UL,u.Used));
        Assert.Equal(0,x.Native.ActiveDmaGrantCount);Assert.Equal(0,x.Native.ActiveOwnedRegionMappingCount);
        Assert.False(x.Kernel.ReadProviderDmaDataMotionResult(original).IsSuccess);
    }
    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(true,true)]
    public void PublicOriginalDmaRequestRejectsReentryAndNeverRepeatsUnknownEffect(bool unknown,bool partialClose)
    {
        (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) x =CopyKernel(true); (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) foreign =CopyKernel(true);
        V6ManagedLocalityCostProvider costs =new V6ManagedLocalityCostProvider(); long now =DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.True(costs.Publish(new(1,"source",3,now,now+60000,64,1,0,0)).IsSuccess);
        Assert.True(costs.Publish(new(1,"destination",5,now,now+60000,64,1,0,0)).IsSuccess);
        PlatformDmaPrepareEvidence sp =x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value;
        PlatformDmaPrepareEvidence dp =x.Kernel.PreparePlatformDmaForDevice(x.DestinationOwner,x.Destination).Value;
        ProviderDmaDataMotionRequest<byte> request =new ProviderDmaDataMotionRequest<byte>(x.Owner,x.DestinationOwner,x.Input,x.Output,x.Plan,x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp);
        Assert.False(x.Kernel.ReadProviderDmaDataMotionResult(request).IsSuccess);
        int copies =0;
        x.Spy.BeforeInvocation=(name,_)=>
        {
            if(name!="CopyDma")return;
            copies++;
            Assert.False(x.Kernel.MoveProviderDmaBuffers(request,costs).IsSuccess);
            Assert.False(x.Kernel.ReadProviderDmaDataMotionResult(request).IsSuccess);
            Assert.False(x.Kernel.CleanupProviderDmaDataMotion(request).IsSuccess);
        };
        x.Spy.AfterOperation=name=>
        {
            if(unknown && name is "CopyDma" or "ResolveDmaCopy")throw new IOException("Original byte reply/query unavailable");
        };
        KernelResult<ProviderDmaDataMotionResult<byte>> moved =x.Kernel.MoveProviderDmaBuffers(request,costs);
        Assert.Equal(!unknown,moved.IsSuccess);Assert.Equal(1,copies);
        if(unknown)
        {
            IDictionary routes =(global::System.Collections.IDictionary)x.Kernel.GetType().GetField("_dmaCopyPayloads",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.GetValue(x.Kernel)!;
            object? originalPayload =routes[x.Source.GrantId];
            Assert.NotNull(originalPayload);
            routes.Remove(x.Source.GrantId);
            ProviderDmaDataMotionRequest<byte> copied =(ProviderDmaDataMotionRequest<byte>)typeof(object).GetMethod("MemberwiseClone",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.Invoke(request,null)!;
            Assert.False(x.Kernel.CleanupProviderDmaDataMotion(copied).IsSuccess);
            Assert.False(foreign.Kernel.CleanupProviderDmaDataMotion(request).IsSuccess);
            PlatformDmaPrepareEvidence foreignPrepare =foreign.Kernel.PreparePlatformDmaForDevice(foreign.Owner,foreign.Source).Value;
            PlatformDmaPrepareEvidence foreignDestinationPrepare =foreign.Kernel.PreparePlatformDmaForDevice(foreign.DestinationOwner,foreign.Destination).Value;
            foreign.Spy.AfterOperation=name=>{if(name is "CopyDma" or "ResolveDmaCopy")throw new IOException("Foreign reply unavailable");};
            Assert.False(foreign.Kernel.MoveProviderDmaBuffers(foreign.Owner,foreign.DestinationOwner,foreign.Input,foreign.Output,foreign.Plan,
                foreign.SourceMap,foreign.Source,foreignPrepare,foreign.DestinationMap,foreign.Destination,foreignDestinationPrepare,costs).IsSuccess);
            IDictionary foreignRoutes =(global::System.Collections.IDictionary)foreign.Kernel.GetType().GetField("_dmaCopyPayloads",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.GetValue(foreign.Kernel)!;
            routes[x.Source.GrantId]=foreignRoutes[foreign.Source.GrantId];
            Assert.False(x.Kernel.CleanupProviderDmaDataMotion(request).IsSuccess);
            Assert.Equal(2,foreign.Native.ActiveDmaGrantCount);
            routes.Remove(x.Source.GrantId);
            foreign.Spy.AfterOperation=null;
            Assert.True(foreign.Kernel.TerminateProcess(foreign.Owner).IsSuccess);
            Assert.True(foreign.Kernel.TerminateProcess(foreign.DestinationOwner).IsSuccess);
            Assert.False(x.Kernel.MoveProviderDmaBuffers(request,costs).IsSuccess);
            Assert.False(x.Kernel.ReadProviderDmaDataMotionResult(request).IsSuccess);
            Assert.Equal(1,copies);
            Assert.Throws<InvalidOperationException>(()=>{_=x.Output.Span.Length;});
            Assert.Throws<InvalidOperationException>(()=>{_=x.Input.Span.Length;});
            Assert.Equal(64UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            x.Spy.AfterOperation=null;
            int retirementCallsBeforeRetry =0;
            if(partialClose)
            {
                x.Spy.AfterOperation=name=>{if(name is "CloseDmaGrant" or "ResolveDmaGrantClose")throw new IOException("Original retirement reply/query lost");};
                Assert.False(x.Kernel.CleanupProviderDmaDataMotion(request).IsSuccess);
                Assert.False(x.Kernel.MoveProviderDmaBuffers(request,costs).IsSuccess);
                Assert.Equal(1,copies);
                Assert.Equal(64UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                retirementCallsBeforeRetry=x.Spy.DmaCloseCalls;
                Assert.Same(originalPayload,routes[x.Source.GrantId]);
                routes.Remove(x.Source.GrantId);
                x.Spy.AfterOperation=null;
            }
            KernelResult cleanup =x.Kernel.CleanupProviderDmaDataMotion(request);Assert.True(cleanup.IsSuccess,cleanup.Message);
            if(partialClose)Assert.Equal(retirementCallsBeforeRetry,x.Spy.DmaCloseCalls);
            Assert.True(x.Kernel.CleanupProviderDmaDataMotion(request).IsSuccess);
            Assert.Empty(routes);
            Assert.Equal(64,x.Input.Span.Length);Assert.Equal(64,x.Output.Span.Length);
            Assert.Equal(0,x.Native.ActiveDmaGrantCount);
            Assert.Equal(64UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(64UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        else Assert.Same(moved.Value,x.Kernel.ReadProviderDmaDataMotionResult(request).Value);
        Assert.True(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(64UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(64,x.Output.Span.Length);
        Assert.True(x.Kernel.TerminateProcess(x.DestinationOwner).IsSuccess);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(1,copies);
        if(!unknown){Assert.Equal(2,foreign.Native.ActiveDmaGrantCount);Assert.Equal(64,foreign.Input.Span.Length);}
    }
    [Theory]
    [InlineData("exact")] [InlineData("absent-cost")] [InlineData("stale-cost")] [InlineData("foreign-owner")]
    [InlineData("unsupported-cost")] [InlineData("throw-cost")]
    public void PublicDmaClientUsesOriginalNativeBackingAndIndependentOwnerCharges(string fault)
    {
        (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) x =CopyKernel(true);
        V6ManagedLocalityCostProvider costs =new V6ManagedLocalityCostProvider();
        long now =DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if(fault!="absent-cost")
        {
            Assert.True(costs.Publish(new(1,"source",fault=="stale-cost"?4UL:3UL,now,now+60000,64,1,0,0)).IsSuccess);
            Assert.True(costs.Publish(new(1,"destination",5,now,now+60000,64,1,0,0)).IsSuccess);
        }
        PlatformDmaPrepareEvidence sp =x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value;
        PlatformDmaPrepareEvidence dp =x.Kernel.PreparePlatformDmaForDevice(x.DestinationOwner,x.Destination).Value;
        ProcessHandle foreign =TestFixtures.Create(x.Kernel,782,1140).Handle;
        int calls =0;x.Spy.BeforeInvocation=(name,_)=>{if(name=="CopyDma")calls++;};
        IV6LocalityCostEvidenceProvider evidence=fault is "unsupported-cost" or "throw-cost" ? new InvalidCostSource(costs,fault) : costs;
        KernelResult<ProviderDmaDataMotionResult<byte>> moved =x.Kernel.MoveProviderDmaBuffers(fault=="foreign-owner"?foreign:x.Owner,x.DestinationOwner,x.Input,x.Output,x.Plan,
            x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp,evidence);
        ulong Used(BudgetAccountHandle account)=>x.Kernel.QueryBudget(account).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        if(fault=="exact")
        {
            Assert.True(moved.IsSuccess,moved.Message);Assert.Same(x.Output,moved.Value!.Buffer);
            Assert.False(moved.Value.Receipt.AuthorizesMovement);Assert.All(moved.Value.Buffer.Span.ToArray(),b=>Assert.Equal((byte)0x5A,b));
            Assert.False(x.Input.IsValid);Assert.Equal(1,calls);Assert.Equal(0UL,Used(x.Budget));Assert.Equal(64UL,Used(x.DestinationBudget));
            Assert.False(x.Kernel.MoveProviderDmaBuffers(x.Owner,x.DestinationOwner,x.Input,x.Output,x.Plan,
                x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp,costs).IsSuccess);
            Assert.Equal(1,calls);Assert.Equal(0,x.Native.ActiveDmaGrantCount);Assert.Equal(0,x.Native.ActiveOwnedRegionMappingCount);
        }
        else
        {
            Assert.False(moved.IsSuccess);Assert.Equal(0,calls);
            Assert.All(x.Output.Span.ToArray(),b=>Assert.Equal((byte)0,b));Assert.True(x.Input.IsValid);
            Assert.Equal(64UL,Used(x.Budget));Assert.Equal(64UL,Used(x.DestinationBudget));
        }
        Assert.True(x.Kernel.TerminateProcess(x.Owner).IsSuccess);Assert.True(x.Kernel.TerminateProcess(x.DestinationOwner).IsSuccess);
        Assert.Equal(0UL,Used(x.Budget));Assert.Equal(0UL,Used(x.DestinationBudget));
        Assert.Equal(0,x.Native.ActiveDmaGrantCount);Assert.Equal(0,x.Native.ActiveOwnedRegionMappingCount);Assert.Equal(0,x.Native.ActiveDeviceLeaseCount);
        Assert.True(x.Kernel.Processes.Resolve(foreign).IsSuccess);
    }

    private sealed class InvalidCostSource(IV6LocalityCostEvidenceProvider original,string fault) : IV6LocalityCostEvidenceProvider
    {
        public KernelResult<ulong> CurrentGeneration(string providerClass)=>original.CurrentGeneration(providerClass);
        public KernelResult<LocalityCostEstimateV1> QueryFresh(string providerClass,long now)
        {
            if(fault=="throw-cost")throw new IOException("Locality source lost.");
            KernelResult<LocalityCostEstimateV1> value =original.QueryFresh(providerClass,now);
            return value.IsSuccess ? KernelResult<LocalityCostEstimateV1>.Ok(value.Value with {Version=99}) : value;
        }
    }
}
