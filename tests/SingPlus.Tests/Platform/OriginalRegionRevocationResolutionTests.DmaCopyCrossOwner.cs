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
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public void OriginalUnknownCrossOwnerCopyDeviceOrMappingDrainRetiresBothLegsAndLeavesOwnedCharge(bool source,bool mapping)
    {
        (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) x = CopyKernel(true); PlatformDmaPrepareEvidence sp = x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value!;
        PlatformDmaPrepareEvidence dp = x.Kernel.PreparePlatformDmaForDevice(x.DestinationOwner,x.Destination).Value!;
        int copies = 0;
        x.Spy.AfterOperation = name => { if(name == "CopyDma") { copies++; throw new IOException("Lost copy response"); } if(name == "ResolveDmaCopy") throw new IOException("Lost query"); };
        Assert.False(x.Kernel.ExecuteV6ProviderDmaDataMotion(x.Owner,x.DestinationOwner,x.Input,x.Output,x.Plan,x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp,() => 3,() => 5).IsSuccess);
        x.Spy.AfterOperation = name => { if(name == "CopyDma") copies++; };
        ProcessHandle subject = source ? x.Owner : x.DestinationOwner;
        KernelResult closed = mapping ? x.Kernel.RevokePlatformRegionMapping(subject,(source ? x.SourceMap : x.DestinationMap).Mapping) :
            x.Kernel.RevokePlatformDevice(subject,(source ? x.Source : x.Destination).DeviceLease);
        Assert.True(closed.IsSuccess,closed.Message); Assert.Equal(0,x.Native.ActiveDmaGrantCount);
        Assert.All(x.Input.Span.ToArray(),b => Assert.Equal((byte)0x5A,b)); Assert.All(x.Output.Span.ToArray(),b => Assert.Equal((byte)0x5A,b));
        Assert.Equal(64UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(64UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.All(x.Kernel.Regions.Snapshot().Where(r => r.Handle == x.Input.Handle || r.Handle == x.Output.Handle),r => Assert.Equal(RegionState.Owned,r.State));
        Assert.True(x.Kernel.TerminateProcess(x.Owner).IsSuccess); Assert.True(x.Kernel.TerminateProcess(x.DestinationOwner).IsSuccess);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(1,copies);
    }

    [Theory]
    [InlineData(false,"none")] [InlineData(true,"none")]
    [InlineData(false,"peer-pin")] [InlineData(true,"peer-pin")]
    [InlineData(false,"peer-history")] [InlineData(true,"peer-history")]
    [InlineData(false,"late-owner")] [InlineData(true,"late-owner")]
    [InlineData(false,"lost-close-query")] [InlineData(true,"lost-close-query")]
    public void OriginalUnknownCrossOwnerCopyTerminationRetiresPeerAndBackingWithoutPeerRegionRelease(bool terminateSource,string fault)
    {
        (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) x = CopyKernel(true);
        PlatformDmaPrepareEvidence sp = x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value!;
        PlatformDmaPrepareEvidence dp = x.Kernel.PreparePlatformDmaForDevice(x.DestinationOwner,x.Destination).Value!;
        int copies = 0;
        x.Spy.AfterOperation = name => { if(name == "CopyDma") { copies++; throw new IOException("Lost byte response"); } if(name == "ResolveDmaCopy") throw new IOException("Lost query"); };
        Assert.False(x.Kernel.ExecuteV6ProviderDmaDataMotion(x.Owner,x.DestinationOwner,x.Input,x.Output,x.Plan,x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp,() => 3,() => 5).IsSuccess);
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        PlatformDmaGrant peer = terminateSource ? x.Destination : x.Source;
        IDictionary grants = (global::System.Collections.IDictionary)x.Kernel.PlatformAuthority.GetType().GetField("_dmaGrants",flags)!.GetValue(x.Kernel.PlatformAuthority)!;
        if (fault == "peer-pin") ((HashSet<PlatformDmaGrantId>)x.Kernel.PlatformAuthority.GetType().GetField("_dmaSubmissionFaultPins",flags)!.GetValue(x.Kernel.PlatformAuthority)!).Add(peer.GrantId);
        if (fault == "peer-history") ((global::System.Collections.IList)grants[peer.GrantId]!.GetType().GetProperty("CopyHistory")!.GetValue(grants[peer.GrantId])!).Clear();
        bool changed = false;
        int closes = 0;
        x.Spy.AfterOperation = name =>
        {
            if(name == "CopyDma") copies++;
            if(name == "CloseDmaGrant") { closes++; if(fault == "lost-close-query") throw new IOException("Lost Close reply"); }
            if(name == "ResolveDmaGrantClose" && fault == "lost-close-query") throw new IOException("Lost Close query");
            if(name == "ResolveDmaCopy" && fault == "late-owner" && !changed)
            { changed = true; grants[peer.GrantId] = typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(grants[peer.GrantId],null); }
        };
        KernelResult terminated = x.Kernel.TerminateProcess(terminateSource ? x.Owner : x.DestinationOwner);
        if (fault is "peer-pin" or "peer-history" or "late-owner")
        {
            Assert.False(terminated.IsSuccess); Assert.Equal(2,x.Native.ActiveDmaGrantCount); Assert.Equal(0,closes);
            Assert.Equal(64UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(64UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.All(x.Kernel.Regions.Snapshot().Where(r => r.Handle == x.Input.Handle || r.Handle == x.Output.Handle),r => Assert.Equal(RegionState.Owned,r.State));
            Assert.Equal(1,copies); return;
        }
        if (fault == "lost-close-query")
        {
            Assert.False(terminated.IsSuccess); Assert.Equal(0,x.Native.ActiveDmaGrantCount);
            Assert.Equal(64UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(64UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            x.Spy.AfterOperation = name => { if(name == "CopyDma") copies++; if(name == "CloseDmaGrant") closes++; };
            Assert.True(x.Kernel.TerminateProcess(terminateSource ? x.Owner : x.DestinationOwner).IsSuccess);
        }
        else Assert.True(terminated.IsSuccess,terminated.Message);
        Assert.Equal(2,closes);
        Assert.Equal(0,x.Native.ActiveDmaGrantCount);
        Assert.All((terminateSource ? x.Output : x.Input).Span.ToArray(),b => Assert.Equal((byte)0x5A,b));
        Assert.Equal(terminateSource ? 0UL : 64UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(terminateSource ? 64UL : 0UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True(x.Kernel.TerminateProcess(terminateSource ? x.DestinationOwner : x.Owner).IsSuccess);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(1,copies);
    }
}
