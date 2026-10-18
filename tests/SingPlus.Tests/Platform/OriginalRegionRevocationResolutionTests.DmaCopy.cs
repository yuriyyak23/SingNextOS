using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Tests.Platform;

public sealed partial class OriginalRegionRevocationResolutionTests
{
    private static (RuntimeKernel Kernel,RuntimeSpy Spy,NeutralDomainRuntimeFacade Native,HybridCpuPlatformAuthorityProvider Provider,ProcessHandle Owner,
        OwnedBuffer<byte> Input,OwnedBuffer<byte> Output,PlatformOwnedRegionSliceMapping SourceMap,PlatformOwnedRegionSliceMapping DestinationMap,
        PlatformDmaGrant Source,PlatformDmaGrant Destination,DataMotionPlanV1 Plan,BudgetAccountHandle Budget,ProcessHandle DestinationOwner,BudgetAccountHandle DestinationBudget) CopyKernel(bool crossOwner = false)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<IResolvingNeutralRuntime,RuntimeSpy>(); var spy = (RuntimeSpy)runtime;
        var native = new NeutralDomainRuntimeFacade(); spy.Inner = native;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime); var kernel = new RuntimeKernel(provider);
        var owner = TestFixtures.Create(kernel,780,1120).Handle; var admin = TestFixtures.Create(kernel,98,998).Handle;
        var cap = kernel.MintCapability(new(998),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin,cap,owner,"native-copy",[new(ServiceBudgetDimension.OwnedMemoryBytes,crossOwner ? 64UL : 128UL)]).Value!.ProcessBudget;
        var destinationOwner = crossOwner ? TestFixtures.Create(kernel,781,1130).Handle : owner;
        var destinationBudget = crossOwner ? kernel.AdmitProcessBudget(admin,cap,destinationOwner,"native-copy-destination",[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]).Value!.ProcessBudget : budget;
        var root = kernel.BindPlatformDomain(owner).Value!;
        var destinationRoot = crossOwner ? kernel.BindPlatformDomain(destinationOwner).Value! : root;
        (OwnedBuffer<byte> Buffer,PlatformOwnedRegionSliceMapping Map,PlatformDmaGrant Grant) Leg(bool read)
        {
            var legOwner = read ? owner : destinationOwner;
            var legRoot = read ? root : destinationRoot;
            var deviceCap = kernel.MintCapability(legRoot.Subject.DomainId,legOwner,ResourceKind.Device,read ? "device/copy-source" : "device/copy-destination",CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!.CapabilityId;
            var boundDevice = kernel.BindPlatformDevice(legOwner,legRoot,deviceCap,PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure);
            Assert.True(boundDevice.IsSuccess,boundDevice.Message); var device = boundDevice.Value!;
            var buffer = kernel.AllocateBuffer<byte>(legOwner,64).Value!;
            var mc = kernel.MintCapability(legRoot.Subject.DomainId,legOwner,ResourceKind.MemoryRegion,CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),CapabilityRights.Map | (read ? CapabilityRights.Read : CapabilityRights.Write)).Value!.CapabilityId;
            var mapped = kernel.MapPlatformOwnedRegionSlice(legOwner,legRoot,mc,buffer.Handle,0,64,read ? PlatformMemoryAccess.Read : PlatformMemoryAccess.Write);
            Assert.True(mapped.IsSuccess,mapped.Message); var map = mapped.Value!;
            var bound = kernel.BindPlatformDma(legOwner,device,map,0,64,read ? PlatformDmaDirection.DeviceReadsMemory : PlatformDmaDirection.DeviceWritesMemory);
            Assert.True(bound.IsSuccess,bound.Message); var grant = bound.Value!;
            return (buffer,map,grant);
        }
        var source = Leg(true); var destination = Leg(false);
        source.Buffer.Span.Fill(0x5A);
        var descriptor = kernel.Regions.Validate(source.Buffer.Handle,new(root.Subject.DomainId,owner.Generation)).Value!;
        var plan = new DataMotionPlanV1(1,"source","destination",3,5,source.Buffer.Handle.Generation.Value,descriptor.MutationEpoch.Value,64,64,DataMotionModeV1.ProviderDmaThenReleaseSource).Validate();
        return (kernel,spy,native,provider,owner,source.Buffer,destination.Buffer,source.Map,destination.Map,source.Grant,destination.Grant,plan,budget,destinationOwner,destinationBudget);
    }

    [Theory]
    [InlineData(false,false,false,"copy")] [InlineData(true,false,false,"copy")]
    [InlineData(false,true,false,"copy")] [InlineData(true,true,false,"copy")]
    [InlineData(false,false,true,"copy")] [InlineData(true,false,true,"copy")]
    [InlineData(false,true,true,"copy")] [InlineData(true,true,true,"copy")]
    [InlineData(false,false,false,"admission")] [InlineData(true,false,false,"admission")]
    [InlineData(false,true,false,"admission")] [InlineData(true,true,false,"admission")]
    [InlineData(false,true,true,"admission")] [InlineData(true,true,true,"admission")]
    public void ActualCopyMalformedNativeHistoryRetainsBackingAndChargesWithoutRepeatedEffect(bool destination,bool afterEffect,bool lostReply,string historyKind)
    {
        var x = CopyKernel();
        var foreign = CopyKernel();
        var sp = x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value!;
        var dp = x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Destination).Value!;
        var calls = 0; Memory<byte> backing = default;
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        void Corrupt(NeutralOriginalDmaCopyRequest request)
        {
            var registry = x.Native.GetType().GetField("_dependencies",flags)!.GetValue(x.Native)!;
            var grants = (global::System.Collections.IDictionary)registry.GetType().GetField("Dma",flags)!.GetValue(registry)!;
            var leg = destination ? request.Destination : request.Source;
            var grant = grants[leg.Grant.Handle]!;
            object historyOwner = grant; var historyName = "OriginalCopies"; var issuedName = "OriginalCopiesIssued";
            if(historyKind == "admission")
            {
                historyOwner = ((global::System.Collections.IDictionary)registry.GetType().GetField("Devices",flags)!.GetValue(registry)!)[leg.Grant.DeviceLease.Handle]!;
                historyName = "OriginalDmaAdmissions"; issuedName = "OriginalDmaIssued";
            }
            var history = (global::System.Collections.IList)historyOwner.GetType().GetProperty(historyName)!.GetValue(historyOwner)!;
            history.Add(history[0]);
            historyOwner.GetType().GetProperty(issuedName)!.SetValue(historyOwner,(ulong)history.Count);
            if(historyKind == "admission")
            {
                Assert.Null(x.Native.ResolveDmaGrantBind(leg.Admission));
                Assert.False(x.Native.BindDmaGrant(leg.Admission with { InvocationId = Guid.NewGuid() }).NoEffectGuaranteed);
                Assert.Equal(2,x.Native.ActiveDmaGrantCount);
            }
        }
        x.Spy.BeforeInvocation = (name,args) =>
        {
            if(name != "CopyDma") return;
            calls++;
            var request = (NeutralOriginalDmaCopyRequest)args![0]!;
            backing = request.DestinationBytes;
            if(afterEffect) return;
            var shared = new byte[64];
            var prior = new NeutralOriginalDmaCopyRequest(Guid.NewGuid(),request.RuntimeIncarnation,request.Source,request.Destination,shared,shared);
            Assert.True(x.Native.CopyDma(prior).NoEffectGuaranteed);
            Corrupt(prior);
        };
        x.Spy.RewriteObservation = value =>
        {
            if(afterEffect && value is NeutralOriginalDmaCopyObservation raw) Corrupt(raw.Request);
            return value;
        };
        x.Spy.AfterOperation = name => { if(lostReply && name == "CopyDma") throw new IOException("Copy reply lost after history corruption"); };
        KernelResult<V6DataMotionExecution<byte>> Move() => x.Kernel.ExecuteV6ProviderDmaDataMotion(x.Owner,x.Owner,x.Input,x.Output,x.Plan,
            x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp,() => 3,() => 5);
        Assert.False(Move().IsSuccess);
        Assert.All(backing.ToArray(),b => Assert.Equal(afterEffect ? (byte)0x5A : (byte)0,b));
        x.Spy.BeforeInvocation = null; x.Spy.RewriteObservation = null; x.Spy.AfterOperation = null;
        Assert.False(Move().IsSuccess); Assert.Equal(1,calls);
        Assert.False(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.False(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.Equal(2,x.Native.ActiveDmaGrantCount); Assert.Equal(0,x.Spy.DmaCloseCalls);
        Assert.Equal(128UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.All(x.Kernel.Regions.Snapshot().Where(r => r.Handle == x.Input.Handle || r.Handle == x.Output.Handle),r => Assert.Equal(RegionState.Owned,r.State));
        Assert.Throws<InvalidOperationException>(() => x.Input.Span.ToArray()); Assert.Throws<InvalidOperationException>(() => x.Output.Span.ToArray());
        Assert.All(backing.ToArray(),b => Assert.Equal(afterEffect ? (byte)0x5A : (byte)0,b));
        Assert.Equal(2,foreign.Native.ActiveDmaGrantCount); Assert.All(foreign.Output.Span.ToArray(),b => Assert.Equal((byte)0,b));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualHybridCpuOriginalCopyMovesBytesAndPureLostReplyReclaimsCharge(bool lostReply)
    {
        var x = CopyKernel(); var sp = x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value!; var dp = x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Destination).Value!;
        var copies = 0; var queries = 0;
        x.Spy.AfterOperation = name => { if (name == "CopyDma") { copies++; if(lostReply) throw new IOException("Lost original byte copy response"); } };
        x.Spy.BeforeOperation = name =>
        {
            if (name == "ResolveDmaCopy") queries++;
            if (name == "CopyDma")
            {
                Assert.Throws<InvalidOperationException>(() => x.Input.Span.ToArray()); Assert.Throws<InvalidOperationException>(() => x.Output.Span.ToArray());
                const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
                Assert.False(Monitor.IsEntered(x.Kernel.GetType().GetField("_platformMemoryUseGate",flags)!.GetValue(x.Kernel)!));
                Assert.False(Monitor.IsEntered(x.Provider.GetType().GetField("_childCreateCommitGate",flags)!.GetValue(x.Provider)!));
            }
        };
        var moved = x.Kernel.ExecuteV6ProviderDmaDataMotion(x.Owner,x.Owner,x.Input,x.Output,x.Plan,x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp,() => 3,() => 5);
        Assert.True(moved.IsSuccess,moved.Message); Assert.Equal(1,copies); Assert.True(queries >= 2);
        Assert.All(moved.Value!.Moved.Span.ToArray(),b => Assert.Equal((byte)0x5A,b));
        Assert.False(x.Input.IsValid); Assert.Equal(0,x.Native.ActiveDmaGrantCount);
        Assert.Equal(64UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        const global::System.Reflection.BindingFlags lookupFlags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var grantOwners = (global::System.Collections.IDictionary)x.Provider.GetType().GetField("_originalDmaGrants",lookupFlags)!.GetValue(x.Provider)!;
        var copyFact = grantOwners.Values.Cast<object>().First().GetType().GetProperty("CopyFact")!.GetValue(grantOwners.Values.Cast<object>().First())!;
        var originalRequest = (PlatformOriginalDmaCopyRequest)copyFact.GetType().GetProperty("Request")!.GetValue(copyFact)!;
        Assert.Throws<InvalidOperationException>(() => originalRequest.Backing);
        Assert.True(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True(x.Provider.ResolveDmaCopy(originalRequest).IsSuccess);
        Assert.Equal(1,copies);
    }

    [Theory]
    [InlineData("query-loss")] [InlineData("malformed-no-effect")] [InlineData("native-cycle")]
    [InlineData("provider-cycle-copy")] [InlineData("provider-history")] [InlineData("source-loss")]
    [InlineData("foreign-realm")]
    public void ActualHybridCpuOriginalCopyUncertainFactsPinBothRegionsAndNeverRepeat(string fault)
    {
        var x = CopyKernel(); var foreign = fault == "foreign-realm" ? CopyKernel() : default;
        var sp = x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value!;
        var dp = x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Destination).Value!;
        var copies = 0;
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        if (fault == "foreign-realm") x.Spy.BeforeOperation = name => { if(name == "CopyDma") x.Spy.Inner = foreign.Native; };
        x.Spy.RewriteObservation = value =>
        {
            if (value is not NeutralOriginalDmaCopyObservation raw) return value;
            if (fault == "malformed-no-effect") return raw with { NoEffectGuaranteed = true, Completed = false };
            if (fault == "native-cycle")
            {
                var registry = x.Native.GetType().GetField("_dependencies",flags)!.GetValue(x.Native)!;
                var grants = (global::System.Collections.IDictionary)registry.GetType().GetField("Dma",flags)!.GetValue(registry)!;
                var grant = grants[raw.Request.Source.Grant.Handle]!;
                var cycle = grant.GetType().GetProperty("Visibility")!.GetValue(grant)!;
                cycle.GetType().GetProperty("Acquired")!.SetValue(cycle,true);
            }
            return value;
        };
        x.Spy.AfterOperation = name =>
        {
            if (name == "ResolveDmaCopy" && fault == "query-loss") throw new IOException("Original query unavailable");
            if (name != "CopyDma") return;
            copies++;
            if (fault == "source-loss") x.Spy.ThrowSource = true;
            if (fault == "provider-cycle-copy")
            {
                var cycles = (global::System.Collections.IDictionary)x.Provider.GetType().GetField("_dmaVisibilityStates",flags)!.GetValue(x.Provider)!;
                var key = cycles.Keys.Cast<object>().First();
                cycles[key] = typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(cycles[key],null);
            }
            if (fault == "provider-history")
            {
                var grants = (global::System.Collections.IDictionary)x.Provider.GetType().GetField("_originalDmaGrants",flags)!.GetValue(x.Provider)!;
                var grant = grants.Values.Cast<object>().First();
                ((global::System.Collections.IList)grant.GetType().GetProperty("CopyHistory")!.GetValue(grant)!).Clear();
            }
            if (fault == "query-loss") throw new IOException("Copy reply lost");
        };
        KernelResult<V6DataMotionExecution<byte>> Move() => x.Kernel.ExecuteV6ProviderDmaDataMotion(x.Owner,x.Owner,x.Input,x.Output,x.Plan,
            x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp,() => 3,() => 5);
        Assert.False(Move().IsSuccess); Assert.Equal(1,copies);
        if (fault == "malformed-no-effect")
        {
            x.Spy.RewriteObservation = null;
            var grants = (global::System.Collections.IDictionary)x.Provider.GetType().GetField("_originalDmaGrants",flags)!.GetValue(x.Provider)!;
            var owner = grants.Values.Cast<object>().First();
            var original = owner.GetType().GetProperty("CopyFact")!.GetValue(owner)!;
            var request = (PlatformOriginalDmaCopyRequest)original.GetType().GetProperty("Request")!.GetValue(original)!;
            Assert.False(x.Provider.SubmitDmaCopy(request).IsSuccess); // A corrected query never overwrites retained malformed raw.
        }
        x.Spy.BeforeOperation = null; x.Spy.AfterOperation = null; x.Spy.RewriteObservation = null; x.Spy.ThrowSource = false; x.Spy.Inner = x.Native;
        Assert.False(Move().IsSuccess); Assert.Equal(1,copies);
        Assert.Throws<InvalidOperationException>(() => x.Input.Span.ToArray()); Assert.Throws<InvalidOperationException>(() => x.Output.Span.ToArray());
        Assert.Equal(128UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        var cleanupPossible = fault is "query-loss" or "native-cycle" or "source-loss";
        Assert.Equal(cleanupPossible,x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.All(x.Kernel.Regions.Snapshot().Where(r => r.Handle == x.Input.Handle || r.Handle == x.Output.Handle),r => Assert.Equal(cleanupPossible ? RegionState.Released : RegionState.Owned,r.State));
        Assert.Equal(cleanupPossible ? 0UL : 128UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(cleanupPossible ? 0 : 2,x.Native.ActiveDmaGrantCount); Assert.Equal(1,copies);
        if (fault == "foreign-realm")
        {
            Assert.Equal(2,foreign.Native.ActiveDmaGrantCount); Assert.Equal(2,x.Native.ActiveDmaGrantCount);
            Assert.All(foreign.Output.Span.ToArray(),b => Assert.Equal((byte)0,b));
        }
    }
}
