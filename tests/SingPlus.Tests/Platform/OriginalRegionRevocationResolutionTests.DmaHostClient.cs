using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed partial class OriginalRegionRevocationResolutionTests
{
    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(true,true)]
    public void PublicOriginalDmaRequestRejectsReentryAndNeverRepeatsUnknownEffect(bool unknown,bool partialClose)
    {
        var x=CopyKernel(true);var foreign=CopyKernel(true);
        var costs=new V6ManagedLocalityCostProvider();var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.True(costs.Publish(new(1,"source",3,now,now+60000,64,1,0,0)).IsSuccess);
        Assert.True(costs.Publish(new(1,"destination",5,now,now+60000,64,1,0,0)).IsSuccess);
        var sp=x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value;
        var dp=x.Kernel.PreparePlatformDmaForDevice(x.DestinationOwner,x.Destination).Value;
        var request=new ProviderDmaDataMotionRequest<byte>(x.Owner,x.DestinationOwner,x.Input,x.Output,x.Plan,x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp);
        Assert.False(x.Kernel.ReadProviderDmaDataMotionResult(request).IsSuccess);
        var copies=0;
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
        var moved=x.Kernel.MoveProviderDmaBuffers(request,costs);
        Assert.Equal(!unknown,moved.IsSuccess);Assert.Equal(1,copies);
        if(unknown)
        {
            var routes=(global::System.Collections.IDictionary)x.Kernel.GetType().GetField("_dmaCopyPayloads",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.GetValue(x.Kernel)!;
            var originalPayload=routes[x.Source.GrantId];
            Assert.NotNull(originalPayload);
            routes.Remove(x.Source.GrantId);
            var copied=(ProviderDmaDataMotionRequest<byte>)typeof(object).GetMethod("MemberwiseClone",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.Invoke(request,null)!;
            Assert.False(x.Kernel.CleanupProviderDmaDataMotion(copied).IsSuccess);
            Assert.False(foreign.Kernel.CleanupProviderDmaDataMotion(request).IsSuccess);
            var foreignPrepare=foreign.Kernel.PreparePlatformDmaForDevice(foreign.Owner,foreign.Source).Value;
            var foreignDestinationPrepare=foreign.Kernel.PreparePlatformDmaForDevice(foreign.DestinationOwner,foreign.Destination).Value;
            foreign.Spy.AfterOperation=name=>{if(name is "CopyDma" or "ResolveDmaCopy")throw new IOException("Foreign reply unavailable");};
            Assert.False(foreign.Kernel.MoveProviderDmaBuffers(foreign.Owner,foreign.DestinationOwner,foreign.Input,foreign.Output,foreign.Plan,
                foreign.SourceMap,foreign.Source,foreignPrepare,foreign.DestinationMap,foreign.Destination,foreignDestinationPrepare,costs).IsSuccess);
            var foreignRoutes=(global::System.Collections.IDictionary)foreign.Kernel.GetType().GetField("_dmaCopyPayloads",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.GetValue(foreign.Kernel)!;
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
            var retirementCallsBeforeRetry=0;
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
            var cleanup=x.Kernel.CleanupProviderDmaDataMotion(request);Assert.True(cleanup.IsSuccess,cleanup.Message);
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
        var x=CopyKernel(true);
        var costs=new V6ManagedLocalityCostProvider();
        var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if(fault!="absent-cost")
        {
            Assert.True(costs.Publish(new(1,"source",fault=="stale-cost"?4UL:3UL,now,now+60000,64,1,0,0)).IsSuccess);
            Assert.True(costs.Publish(new(1,"destination",5,now,now+60000,64,1,0,0)).IsSuccess);
        }
        var sp=x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value;
        var dp=x.Kernel.PreparePlatformDmaForDevice(x.DestinationOwner,x.Destination).Value;
        var foreign=TestFixtures.Create(x.Kernel,782,1140).Handle;
        var calls=0;x.Spy.BeforeInvocation=(name,_)=>{if(name=="CopyDma")calls++;};
        IV6LocalityCostEvidenceProvider evidence=fault is "unsupported-cost" or "throw-cost" ? new InvalidCostSource(costs,fault) : costs;
        var moved=x.Kernel.MoveProviderDmaBuffers(fault=="foreign-owner"?foreign:x.Owner,x.DestinationOwner,x.Input,x.Output,x.Plan,
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
            var value=original.QueryFresh(providerClass,now);
            return value.IsSuccess ? KernelResult<LocalityCostEstimateV1>.Ok(value.Value with {Version=99}) : value;
        }
    }
}
