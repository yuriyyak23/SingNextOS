using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed partial class OriginalRegionRevocationResolutionTests
{
    [Theory]
    [InlineData("history")] [InlineData("owner-copy")] [InlineData("independent-pin")]
    [InlineData("query-loss")] [InlineData("reentry")] [InlineData("close-reply")]
    public void OriginalUnknownCopyCleanupConsumesOnlyItsOwnLifetimeAndRetriesWithoutBytes(string fault)
    {
        var x = CopyKernel(); var sp = x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value!;
        var dp = x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Destination).Value!;
        var copies = 0;
        x.Spy.AfterOperation = name => { if(name == "CopyDma") { copies++; throw new IOException("Lost byte response"); } if(name == "ResolveDmaCopy") throw new IOException("Lost byte query"); };
        Assert.False(x.Kernel.ExecuteV6ProviderDmaDataMotion(x.Owner,x.Owner,x.Input,x.Output,x.Plan,x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp,() => 3,() => 5).IsSuccess);
        Assert.Equal(1,copies);
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var grants = (global::System.Collections.IDictionary)x.Kernel.PlatformAuthority.GetType().GetField("_dmaGrants",flags)!.GetValue(x.Kernel.PlatformAuthority)!;
        var owner = grants[x.Source.GrantId]!;
        var original = owner.GetType().GetProperty("OriginalCopy")!.GetValue(owner)!;
        if(fault == "history") ((global::System.Collections.IList)owner.GetType().GetProperty("CopyHistory")!.GetValue(owner)!).Clear();
        if(fault == "owner-copy") owner.GetType().GetProperty("OriginalCopy")!.SetValue(owner,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null));
        if(fault == "independent-pin")
            ((HashSet<PlatformDmaGrantId>)x.Kernel.PlatformAuthority.GetType().GetField("_dmaSubmissionFaultPins",flags)!.GetValue(x.Kernel.PlatformAuthority)!).Add(x.Source.GrantId);
        var injected = false;
        x.Spy.AfterOperation = name =>
        {
            if(name == "CopyDma") copies++;
            if (injected) return;
            if (fault == "query-loss" && name == "ResolveDmaCopy") { injected = true; throw new IOException("Cleanup query unavailable"); }
            if (fault == "reentry" && name == "ResolveDmaCopy")
            {
                injected = true;
                Assert.False(Monitor.IsEntered(x.Kernel.GetType().GetField("_platformMemoryUseGate",flags)!.GetValue(x.Kernel)!));
                Assert.False(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
            }
            if (fault == "close-reply" && name == "CloseDmaGrant") { injected = true; throw new IOException("Lost original grant Close response"); }
        };
        var first = x.Kernel.TerminateProcess(x.Owner);
        var impossible = fault is "history" or "owner-copy" or "independent-pin";
        if (impossible)
        {
            Assert.False(first.IsSuccess); Assert.Equal(2,x.Native.ActiveDmaGrantCount);
            Assert.Equal(128UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.False((bool)original.GetType().GetProperty("LifetimeConsumed")!.GetValue(original)!);
        }
        else
        {
            if (fault == "query-loss")
            {
                Assert.False(first.IsSuccess); Assert.Equal(0,x.Native.ActiveDmaGrantCount);
                Assert.Equal(128UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            }
            x.Spy.AfterOperation = name => { if(name == "CopyDma") copies++; };
            Assert.True(first.IsSuccess || x.Kernel.TerminateProcess(x.Owner).IsSuccess);
            Assert.Equal(0,x.Native.ActiveDmaGrantCount);
            Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.All(x.Kernel.Regions.Snapshot().Where(r => r.Handle == x.Input.Handle || r.Handle == x.Output.Handle),r => Assert.Equal(RegionState.Released,r.State));
            Assert.True((bool)original.GetType().GetProperty("LifetimeConsumed")!.GetValue(original)!);
        }
        Assert.Equal(1,copies);
    }
    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public void ConsumedProviderCopyLifetimeRequiresItsRetainedCanonicalNativeRaw(bool copy,bool duringFinal)
    {
        var x=CopyKernel();var foreign=CopyKernel();
        var sp=x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value!;
        var dp=x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Destination).Value!;
        var copies=0;
        x.Spy.AfterOperation=name=>{if(name=="CopyDma"){copies++;throw new IOException("Lost byte reply");}if(name=="ResolveDmaCopy")throw new IOException("Lost byte query");};
        Assert.False(x.Kernel.ExecuteV6ProviderDmaDataMotion(x.Owner,x.Owner,x.Input,x.Output,x.Plan,x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp,()=>3,()=>5).IsSuccess);
        x.Spy.AfterOperation=name=>{if(name=="CopyDma")copies++;};
        const global::System.Reflection.BindingFlags flags=global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic;
        var grants=(global::System.Collections.IDictionary)x.Provider.GetType().GetField("_originalDmaGrants",flags)!.GetValue(x.Provider)!;
        var source=grants.Values.Cast<object>().First();
        var fact=source.GetType().GetProperty("CopyFact")!.GetValue(source)!;
        var request=(PlatformOriginalDmaCopyRequest)fact.GetType().GetProperty("Request")!.GetValue(fact)!;
        var rawProperty=fact.GetType().GetProperty("Raw")!;
        if(duringFinal)
        {
            var queries=0;object? capturedRaw=null;var callbackLocksFree=false;
            x.Spy.AfterOperation=name=>
            {
                if(name=="CopyDma")copies++;
                if(name!="ResolveDmaCopy" || ++queries!=2)return;
                callbackLocksFree=!Monitor.IsEntered(x.Provider.GetType().GetField("_childCreateCommitGate",flags)!.GetValue(x.Provider)!);
                capturedRaw=rawProperty.GetValue(fact)!;
                rawProperty.SetValue(fact,copy?typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(capturedRaw,null):null);
            };
            Assert.False(x.Provider.ResolveDmaCopyLifetime(request).IsSuccess);
            Assert.True(callbackLocksFree);Assert.NotNull(capturedRaw);
            Assert.False((bool)fact.GetType().GetProperty("LifetimeConsumed")!.GetValue(fact)!);
            Assert.Equal(128UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            rawProperty.SetValue(fact,capturedRaw);
            x.Spy.AfterOperation=name=>{if(name=="CopyDma")copies++;};
        }
        Assert.True(x.Provider.ResolveDmaCopyLifetime(request).IsSuccess);
        var originalRaw=rawProperty.GetValue(fact)!;
        rawProperty.SetValue(fact,copy?typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(originalRaw,null):null);
        Assert.False(x.Provider.ResolveDmaCopyLifetime(request).IsSuccess);
        Assert.False(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.Equal(128UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.All(x.Kernel.Regions.Snapshot().Where(r=>r.Handle==x.Input.Handle||r.Handle==x.Output.Handle),r=>Assert.Equal(RegionState.Owned,r.State));
        Assert.Equal(2,foreign.Native.ActiveDmaGrantCount);
        Assert.Equal(128UL,foreign.Kernel.QueryBudget(foreign.Budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        rawProperty.SetValue(fact,originalRaw);
        Assert.True(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.Equal(1,copies);Assert.Equal(0,x.Native.ActiveDmaGrantCount);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.All(x.Kernel.Regions.Snapshot().Where(r=>r.Handle==x.Input.Handle||r.Handle==x.Output.Handle),r=>Assert.Equal(RegionState.Released,r.State));
        Assert.Equal(2,foreign.Native.ActiveDmaGrantCount);
        Assert.All(foreign.Kernel.Regions.Snapshot().Where(r=>r.Handle==foreign.Input.Handle||r.Handle==foreign.Output.Handle),r=>Assert.Equal(RegionState.Owned,r.State));
    }    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CrossOwnerConsumedLifetimeLossRetainsBothOriginalReservationsUntilExactRestore(bool foreignRaw)
    {
        var x=CopyKernel(true);var foreign=CopyKernel(true);
        const global::System.Reflection.BindingFlags flags=global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic;
        object Original(RuntimeKernel kernel,PlatformDmaGrant source)
        {
            var grants=(global::System.Collections.IDictionary)kernel.PlatformAuthority.GetType().GetField("_dmaGrants",flags)!.GetValue(kernel.PlatformAuthority)!;
            return grants[source.GrantId]!.GetType().GetProperty("OriginalCopy")!.GetValue(grants[source.GrantId])!;
        }
        var foreignSp=foreign.Kernel.PreparePlatformDmaForDevice(foreign.Owner,foreign.Source).Value!;
        var foreignDp=foreign.Kernel.PreparePlatformDmaForDevice(foreign.DestinationOwner,foreign.Destination).Value!;
        var foreignCopies=0;
        foreign.Spy.AfterOperation=name=>{if(name=="CopyDma"){foreignCopies++;throw new IOException("Foreign byte reply lost");}if(name=="ResolveDmaCopy")throw new IOException("Foreign byte query lost");};
        Assert.False(foreign.Kernel.ExecuteV6ProviderDmaDataMotion(foreign.Owner,foreign.DestinationOwner,foreign.Input,foreign.Output,foreign.Plan,foreign.SourceMap,foreign.Source,foreignSp,foreign.DestinationMap,foreign.Destination,foreignDp,()=>3,()=>5).IsSuccess);
        var foreignOriginal=Original(foreign.Kernel,foreign.Source);
        var foreignRequest=(PlatformOriginalDmaCopyRequest)foreignOriginal.GetType().GetProperty("Request")!.GetValue(foreignOriginal)!;
        var sp=x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value!;var dp=x.Kernel.PreparePlatformDmaForDevice(x.DestinationOwner,x.Destination).Value!;
        var copies=0;var closes=0;
        x.Spy.AfterOperation=name=>{if(name=="CopyDma"){copies++;throw new IOException("Byte reply lost");}if(name=="ResolveDmaCopy")throw new IOException("Byte query lost");};
        Assert.False(x.Kernel.ExecuteV6ProviderDmaDataMotion(x.Owner,x.DestinationOwner,x.Input,x.Output,x.Plan,x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp,()=>3,()=>5).IsSuccess);
        var original=Original(x.Kernel,x.Source);
        x.Spy.AfterOperation=name=>{if(name=="CopyDma")copies++;if(name=="CloseDmaGrant"){closes++;throw new IOException("Grant Close reply lost");}if(name=="ResolveDmaGrantClose")throw new IOException("Grant Close query lost");};
        Assert.False(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.True((bool)original.GetType().GetProperty("LifetimeConsumed")!.GetValue(original)!);
        var lifetime=original.GetType().GetProperty("LifetimeRaw")!;var retained=lifetime.GetValue(original);
        Assert.NotNull(retained);
        lifetime.SetValue(original,foreignRaw?PlatformAuthorityResult<PlatformOriginalDmaCopyLifetime>.Ok(new(foreignRequest,true)):null);
        x.Spy.AfterOperation=name=>{if(name=="CopyDma")copies++;if(name=="CloseDmaGrant")closes++;};
        Assert.False(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.Equal(64UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(64UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.All(x.Kernel.Regions.Snapshot().Where(r=>r.Handle==x.Input.Handle||r.Handle==x.Output.Handle),r=>Assert.Equal(RegionState.Owned,r.State));
        lifetime.SetValue(original,retained);
        Assert.True(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(64UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.All(x.Output.Span.ToArray(),b=>Assert.Equal((byte)0x5A,b));
        Assert.True(x.Kernel.TerminateProcess(x.DestinationOwner).IsSuccess);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(1,copies);Assert.Equal(2,closes);Assert.Equal(0,x.Native.ActiveDmaGrantCount);
        Assert.Equal(1,foreignCopies);Assert.Equal(2,foreign.Native.ActiveDmaGrantCount);
        Assert.All(foreign.Kernel.Regions.Snapshot().Where(r=>r.Handle==foreign.Input.Handle||r.Handle==foreign.Output.Handle),r=>Assert.Equal(RegionState.Owned,r.State));
    }}
