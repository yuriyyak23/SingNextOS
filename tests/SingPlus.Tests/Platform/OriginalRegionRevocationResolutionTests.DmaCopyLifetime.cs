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
}
