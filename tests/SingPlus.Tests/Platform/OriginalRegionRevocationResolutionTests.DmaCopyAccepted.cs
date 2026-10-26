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
    [InlineData("copy-leg",false,false,false)] [InlineData("copy-leg",true,false,false)]
    [InlineData("acquire",false,false,false)] [InlineData("acquire",true,false,false)]
    [InlineData("copy-leg",true,true,false)] [InlineData("copy-leg",false,true,false)]
    [InlineData("copy-leg",true,true,true)]
    public void ActualCopyVisibilityHistoryRefusesEffectAndPreservesExactClosureConsequences(string stage,bool lostReply,bool lostQuery,bool independentPin)
    {
        (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) x = CopyKernel(); (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) foreign = CopyKernel();
        PlatformDmaPrepareEvidence sp = x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value!;
        PlatformDmaPrepareEvidence dp = x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Destination).Value!;
        int copies = 0; int acquisitions = 0; Memory<byte> backing = default;
        YAKSys_Hybrid_CPU.Core.NeutralOriginalDmaCopyRequest? nativeRequest = null;
        object? cycle = null; object? revision = null;
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        void Corrupt(YAKSys_Hybrid_CPU.Core.NeutralDmaGrant grant)
        {
            object registry = x.Native.GetType().GetField("_dependencies",flags)!.GetValue(x.Native)!;
            IDictionary grants = (global::System.Collections.IDictionary)registry.GetType().GetField("Dma",flags)!.GetValue(registry)!;
            object owner = grants[grant.Handle]!;
            IList history = (global::System.Collections.IList)owner.GetType().GetProperty("OriginalVisibilityHistory")!.GetValue(owner)!;
            history.Add(history[0]); owner.GetType().GetProperty("OriginalVisibilityIssued")!.SetValue(owner,(ulong)history.Count);
            cycle = owner.GetType().GetProperty("Visibility")!.GetValue(owner)!;
            revision = cycle.GetType().GetProperty("Revision")!.GetValue(cycle);
        }
        x.Spy.BeforeInvocation = (name,args) =>
        {
            if(name == "CopyDma")
            {
                copies++;
                NeutralOriginalDmaCopyRequest request = (YAKSys_Hybrid_CPU.Core.NeutralOriginalDmaCopyRequest)args![0]!;
                backing = request.DestinationBytes; nativeRequest = request;
                if(stage == "copy-leg") Corrupt(request.Destination.Grant);
            }
            if(stage == "acquire" && name == "AcquireDmaVisibility" && args![0] is YAKSys_Hybrid_CPU.Core.NeutralOriginalDmaVisibilityRequest visibility)
            { acquisitions++; Corrupt(visibility.Grant); }
        };
        x.Spy.AfterOperation = name =>
        {
            if(lostReply && name == (stage == "copy-leg" ? "CopyDma" : "AcquireDmaVisibility")) throw new IOException("Original effect reply lost");
            if(lostQuery && name == "ResolveDmaCopy") throw new IOException("Original refusal query lost");
        };
        KernelResult<V6DataMotionExecution<byte>> Move() => x.Kernel.ExecuteV6ProviderDmaDataMotion(x.Owner,x.Owner,x.Input,x.Output,x.Plan,
            x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp,() => 3,() => 5);
        KernelResult<V6DataMotionExecution<byte>> moved = Move(); Assert.False(moved.IsSuccess);
        Assert.Equal(1,copies); Assert.Equal(stage == "acquire" ? 1 : 0,acquisitions);
        Assert.NotNull(cycle); Assert.Equal(revision,cycle!.GetType().GetProperty("Revision")!.GetValue(cycle));
        if(stage == "copy-leg" && !lostQuery)
        {
            Assert.Throws<InvalidOperationException>(() => backing.ToArray());
            Assert.All(x.Output.Span.ToArray(),b => Assert.Equal((byte)0,b));
            Assert.Equal(64,x.Input.Span.Length);
            NeutralOriginalDmaCopyObservation refusal = x.Native.ResolveDmaCopy(nativeRequest!)!;
            Assert.True(refusal.NoEffectGuaranteed); Assert.True(refusal.NoFurtherByteUseGuaranteed);
            Assert.Same(refusal,x.Native.CopyDma(nativeRequest!));
            Assert.Null(x.Native.ValidateDmaCopy(nativeRequest!));
        }
        else Assert.All(backing.ToArray(),b => Assert.Equal(stage == "copy-leg" ? (byte)0 : (byte)0x5A,b));
        Assert.Equal(128UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        x.Spy.BeforeInvocation = null; x.Spy.AfterOperation = null;
        if(stage == "acquire" || lostQuery) { Assert.False(Move().IsSuccess); Assert.Equal(1,copies); }
        if(independentPin)
        {
            ((HashSet<PlatformDmaGrantId>)x.Kernel.PlatformAuthority.GetType().GetField("_dmaSubmissionFaultPins",flags)!.GetValue(x.Kernel.PlatformAuthority)!).Add(x.Source.GrantId);
            Assert.False(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
            Assert.Equal(2,x.Native.ActiveDmaGrantCount); Assert.Equal(0,x.Spy.DmaCloseCalls);
            Assert.Equal(128UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.All(x.Kernel.Regions.Snapshot().Where(r => r.Handle == x.Input.Handle || r.Handle == x.Output.Handle),r => Assert.Equal(RegionState.Owned,r.State));
            Assert.Equal(2,foreign.Native.ActiveDmaGrantCount); return;
        }
        KernelResult terminated = x.Kernel.TerminateProcess(x.Owner);
        Assert.True(terminated.IsSuccess,terminated.Message);
        Assert.Equal(2,x.Spy.DmaCloseCalls); Assert.Equal(0,x.Native.ActiveDmaGrantCount);
        Assert.All(x.Kernel.Regions.Snapshot().Where(r => r.Handle == x.Input.Handle || r.Handle == x.Output.Handle),r => Assert.Equal(RegionState.Released,r.State));
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(2,foreign.Native.ActiveDmaGrantCount); Assert.All(foreign.Output.Span.ToArray(),b => Assert.Equal((byte)0,b));
    }

    [Theory]
    [InlineData("completion",false)] [InlineData("visibility",false)] [InlineData("publication",false)]
    [InlineData("completion",true)] [InlineData("visibility",true)] [InlineData("publication",true)]
    public void OriginalAcceptedCopyUnknownObservationOrPublicationCanRetireBytesWithoutPublishing(string fault,bool crossOwner)
    {
        (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) x = CopyKernel(crossOwner); PlatformDmaPrepareEvidence sp = x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value!;
        PlatformDmaPrepareEvidence dp = x.Kernel.PreparePlatformDmaForDevice(x.DestinationOwner,x.Destination).Value!;
        int copies = 0; int validations = 0;
        x.Spy.AfterOperation = name =>
        {
            if(name == "CopyDma") copies++;
            if(fault == "completion" && name == "ResolveDmaCopy") throw new IOException("Lost pure completion lookup");
            if(fault == "visibility" && name is "AcquireDmaVisibility" or "ResolveDmaVisibility") throw new IOException("Lost acquire/lookup");
            if(fault == "publication" && name == "ValidateDmaCopy" && ++validations == 2) Assert.False(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        };
        KernelResult<V6DataMotionExecution<byte>> moved = x.Kernel.ExecuteV6ProviderDmaDataMotion(x.Owner,x.DestinationOwner,x.Input,x.Output,x.Plan,x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp,() => 3,() => 5);
        Assert.False(moved.IsSuccess); Assert.Equal(1,copies);
        Assert.Equal(crossOwner ? 64UL : 128UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        x.Spy.AfterOperation = name => { if(name == "CopyDma") copies++; };
        KernelResult closed = x.Kernel.TerminateProcess(x.Owner);
        Assert.True(closed.IsSuccess,closed.Message); Assert.Equal(0,x.Native.ActiveDmaGrantCount);
        Assert.Equal(RegionState.Released,x.Kernel.Regions.Snapshot().Single(r => r.Handle == x.Input.Handle).State);
        Assert.Equal(crossOwner ? RegionState.Owned : RegionState.Released,x.Kernel.Regions.Snapshot().Single(r => r.Handle == x.Output.Handle).State);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(1,copies);
        if (crossOwner)
        {
            Assert.All(x.Output.Span.ToArray(),b => Assert.Equal((byte)0x5A,b));
            Assert.Equal(64UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.True(x.Kernel.TerminateProcess(x.DestinationOwner).IsSuccess);
            Assert.Equal(0UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
    }

    [Theory]
    [InlineData("record-copy")] [InlineData("missing-leg")] [InlineData("independent-pin")]
    [InlineData("page-user")] [InlineData("completed-page-user")] [InlineData("visibility-history")]
    [InlineData("query-loss")] [InlineData("reentry")]
    public void AcceptedOriginalCopyCleanupPreservesIndependentUsersAndRetriesItsOwnLookup(string fault)
    {
        (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) x = CopyKernel(); PlatformDmaPrepareEvidence sp = x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value!;
        PlatformDmaPrepareEvidence dp = x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Destination).Value!;
        int copies = 0;
        bool visibility = fault == "visibility-history";
        x.Spy.AfterOperation = name =>
        {
            if(name == "CopyDma") copies++;
            if(!visibility && name == "ResolveDmaCopy") throw new IOException("Lost completion query");
            if(visibility && name is "AcquireDmaVisibility" or "ResolveDmaVisibility") throw new IOException("Lost acquire query");
        };
        Assert.False(x.Kernel.ExecuteV6ProviderDmaDataMotion(x.Owner,x.Owner,x.Input,x.Output,x.Plan,x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp,() => 3,() => 5).IsSuccess);
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        PlatformAuthorityBridge bridge = x.Kernel.PlatformAuthority;
        IDictionary active = (global::System.Collections.IDictionary)bridge.GetType().GetField("_activeDmaSubmissions",flags)!.GetValue(bridge)!;
        object original = active[x.Destination.GrantId]!;
        if(fault == "record-copy") active[x.Destination.GrantId] = typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null);
        if(fault == "missing-leg") active.Remove(x.Destination.GrantId);
        if(fault == "independent-pin") ((HashSet<PlatformDmaGrantId>)bridge.GetType().GetField("_dmaSubmissionFaultPins",flags)!.GetValue(bridge)!).Add(x.Destination.GrantId);
        if(fault == "page-user") ((HashSet<ulong>)original.GetType().GetProperty("PageFaultsInFlight")!.GetValue(original)!).Add(1);
        if(fault == "completed-page-user") ((HashSet<ulong>)original.GetType().GetProperty("ResolvedPageFaults")!.GetValue(original)!).Add(1);
        if(visibility)
        {
            IDictionary grants = (global::System.Collections.IDictionary)bridge.GetType().GetField("_dmaGrants",flags)!.GetValue(bridge)!;
            object owner = grants[x.Destination.GrantId]!;
            ((global::System.Collections.IList)owner.GetType().GetProperty("VisibilityHistory")!.GetValue(owner)!).Clear();
        }
        bool injected = false;
        x.Spy.AfterOperation = name =>
        {
            if(name == "CopyDma") copies++;
            if(name != "ResolveDmaCopy" || injected) return;
            injected = true;
            if(fault == "query-loss") throw new IOException("Lost cleanup lookup");
            if(fault == "reentry")
            {
                Assert.False(Monitor.IsEntered(x.Kernel.GetType().GetField("_platformMemoryUseGate",flags)!.GetValue(x.Kernel)!));
                Assert.False(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
            }
        };
        KernelResult closed = x.Kernel.TerminateProcess(x.Owner);
        if(fault is "query-loss" or "reentry")
        {
            x.Spy.AfterOperation = name => { if(name == "CopyDma") copies++; };
            Assert.True(closed.IsSuccess || x.Kernel.TerminateProcess(x.Owner).IsSuccess);
            Assert.Equal(0,x.Native.ActiveDmaGrantCount);
            Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        else
        {
            Assert.False(closed.IsSuccess); Assert.Equal(2,x.Native.ActiveDmaGrantCount);
            Assert.All(x.Kernel.Regions.Snapshot().Where(r => r.Handle == x.Input.Handle || r.Handle == x.Output.Handle),r => Assert.Equal(RegionState.Owned,r.State));
            Assert.Equal(128UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        Assert.Equal(1,copies);
    }
}

