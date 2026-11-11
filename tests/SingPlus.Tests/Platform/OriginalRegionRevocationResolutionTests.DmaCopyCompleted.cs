using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Tests.Platform;

public sealed partial class OriginalRegionRevocationResolutionTests
{
    [Theory]
    [InlineData(false,false,1)] [InlineData(true,false,1)] [InlineData(false,true,1)] [InlineData(true,true,1)]
    [InlineData(false,false,2)] [InlineData(true,false,2)] [InlineData(false,true,2)] [InlineData(true,true,2)]
    public void CompletedOriginalCopyDeviceOrMappingDrainRetiresBackingButNotOwnedCharge(bool source,bool mapping,int lostClose)
    {
        (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) x = CopyKernel(true); PlatformDmaPrepareEvidence sp = x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value!;
        PlatformDmaPrepareEvidence dp = x.Kernel.PreparePlatformDmaForDevice(x.DestinationOwner,x.Destination).Value!;
        int copies = 0; int closes = 0;
        x.Spy.AfterOperation = name =>
        {
            if(name == "CopyDma") copies++;
            if(name == "CloseDmaGrant" && ++closes == lostClose) throw new IOException("Lost Close response");
            if(name == "ResolveDmaGrantClose") throw new IOException("Lost Close lookup");
        };
        Assert.False(x.Kernel.ExecuteV6ProviderDmaDataMotion(x.Owner,x.DestinationOwner,x.Input,x.Output,x.Plan,x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp,() => 3,() => 5).IsSuccess);
        x.Spy.AfterOperation = name => { if(name == "CopyDma") copies++; if(name == "CloseDmaGrant") closes++; };
        ProcessHandle owner = source ? x.Owner : x.DestinationOwner;
        KernelResult closed = mapping ? x.Kernel.RevokePlatformRegionMapping(owner,(source ? x.SourceMap : x.DestinationMap).Mapping) :
            x.Kernel.RevokePlatformDevice(owner,(source ? x.Source : x.Destination).DeviceLease);
        Assert.True(closed.IsSuccess,closed.Message); Assert.Equal(0,x.Native.ActiveDmaGrantCount); Assert.Equal(2,closes); Assert.Equal(1,copies);
        Assert.All(x.Input.Span.ToArray(),b => Assert.Equal((byte)0x5A,b)); Assert.All(x.Output.Span.ToArray(),b => Assert.Equal((byte)0x5A,b));
        Assert.Equal(64UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(64UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.All(x.Kernel.Regions.Snapshot().Where(r => r.Handle == x.Input.Handle || r.Handle == x.Output.Handle),r => Assert.Equal(RegionState.Owned,r.State));
        Assert.True(x.Kernel.TerminateProcess(x.Owner).IsSuccess); Assert.True(x.Kernel.TerminateProcess(x.DestinationOwner).IsSuccess);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Theory]
    [InlineData("record-copy")] [InlineData("missing-leg")] [InlineData("independent-pin")]
    [InlineData("page-user")] [InlineData("source-revision")] [InlineData("copy-history")]
    [InlineData("late-owner")] [InlineData("query-loss")] [InlineData("reentry")]
    public void CompletedOriginalCopyRetirementKeepsIndependentHistoryPinsAndRetriesOnlyLookup(string fault)
    {
        (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) x = CopyKernel(true); PlatformDmaPrepareEvidence sp = x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value!;
        PlatformDmaPrepareEvidence dp = x.Kernel.PreparePlatformDmaForDevice(x.DestinationOwner,x.Destination).Value!;
        int copies = 0; int closes = 0;
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        PlatformAuthorityBridge bridge = x.Kernel.PlatformAuthority;
        IDictionary grants = (global::System.Collections.IDictionary)bridge.GetType().GetField("_dmaGrants",flags)!.GetValue(bridge)!;
        object destination = grants[x.Destination.GrantId]!;
        PropertyInfo completed = destination.GetType().GetProperty("CompletedCopySubmission")!;
        bool injected = false;
        x.Spy.AfterOperation = name =>
        {
            if(name == "CopyDma") copies++;
            if(name == "CloseDmaGrant") { closes++; throw new IOException("Lost Close response"); }
            if(name == "ResolveDmaGrantClose") throw new IOException("Lost Close lookup");
            if(name != "ResolveDmaCopy" || injected || completed.GetValue(destination) is not { } record) return;
            injected = true;
            if(fault == "record-copy") completed.SetValue(destination,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(record,null));
            if(fault == "missing-leg") completed.SetValue(destination,null);
            if(fault == "independent-pin") ((HashSet<PlatformDmaGrantId>)bridge.GetType().GetField("_dmaSubmissionFaultPins",flags)!.GetValue(bridge)!).Add(x.Destination.GrantId);
            if(fault == "page-user") ((HashSet<ulong>)record.GetType().GetProperty("PageFaultsInFlight")!.GetValue(record)!).Add(1);
            if(fault == "source-revision") destination.GetType().GetProperty("SourceRevision")!.SetValue(destination,ulong.MaxValue);
            if(fault == "copy-history") ((global::System.Collections.IList)destination.GetType().GetProperty("CopyHistory")!.GetValue(destination)!).Clear();
            if(fault == "late-owner") grants[x.Destination.GrantId] = typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(destination,null);
            if(fault == "query-loss") throw new IOException("Lost lifetime lookup");
            if(fault == "reentry") Assert.False(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        };
        Assert.False(x.Kernel.ExecuteV6ProviderDmaDataMotion(x.Owner,x.DestinationOwner,x.Input,x.Output,x.Plan,x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp,() => 3,() => 5).IsSuccess);
        Assert.True(injected);
        x.Spy.AfterOperation = name => { if(name == "CopyDma") copies++; if(name == "CloseDmaGrant") closes++; };
        KernelResult result = x.Kernel.TerminateProcess(x.Owner);
        if(fault is "query-loss" or "reentry")
        {
            x.Spy.AfterOperation = name => { if(name == "CopyDma") copies++; if(name == "CloseDmaGrant") closes++; };
            Assert.True(result.IsSuccess || x.Kernel.TerminateProcess(x.Owner).IsSuccess);
            Assert.Equal(0,x.Native.ActiveDmaGrantCount); Assert.Equal(2,closes);
            Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.All(x.Output.Span.ToArray(),b => Assert.Equal((byte)0x5A,b));
        }
        else
        {
            Assert.False(result.IsSuccess); Assert.Equal(2,x.Native.ActiveDmaGrantCount); Assert.Equal(0,closes);
            Assert.Equal(64UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Throws<InvalidOperationException>(() => x.Output.Span.ToArray());
        }
        Assert.Equal(64UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(1,copies);
    }

    [Theory]
    [InlineData(false,1)] [InlineData(true,1)] [InlineData(false,2)] [InlineData(true,2)]
    public void CompletedOriginalCopyLateCloseLossCanRetireBothLegsWithoutNewPublication(bool terminateSource,int lostClose)
    {
        (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) x = CopyKernel(true);
        PlatformDmaPrepareEvidence sp = x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value!;
        PlatformDmaPrepareEvidence dp = x.Kernel.PreparePlatformDmaForDevice(x.DestinationOwner,x.Destination).Value!;
        int copies = 0; int closes = 0;
        x.Spy.AfterOperation = name =>
        {
            if(name == "CopyDma") copies++;
            if(name == "CloseDmaGrant" && ++closes == lostClose) throw new IOException("Lost completed-pair Close reply");
            if(name == "ResolveDmaGrantClose") throw new IOException("Lost completed-pair Close query");
        };
        Assert.False(x.Kernel.ExecuteV6ProviderDmaDataMotion(x.Owner,x.DestinationOwner,x.Input,x.Output,x.Plan,x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp,() => 3,() => 5).IsSuccess);
        Assert.Equal(1,copies);
        Assert.Equal(64UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(64UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        x.Spy.AfterOperation = name => { if(name == "CopyDma") copies++; if(name == "CloseDmaGrant") closes++; };
        KernelResult terminated = x.Kernel.TerminateProcess(terminateSource ? x.Owner : x.DestinationOwner);
        Assert.True(terminated.IsSuccess,terminated.Message);
        Assert.Equal(0,x.Native.ActiveDmaGrantCount); Assert.Equal(2,closes); Assert.Equal(1,copies);
        Assert.Equal(terminateSource ? 0UL : 64UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(terminateSource ? 64UL : 0UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.All((terminateSource ? x.Output : x.Input).Span.ToArray(),b => Assert.Equal((byte)0x5A,b));
        Assert.True(x.Kernel.TerminateProcess(terminateSource ? x.DestinationOwner : x.Owner).IsSuccess);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }
}
