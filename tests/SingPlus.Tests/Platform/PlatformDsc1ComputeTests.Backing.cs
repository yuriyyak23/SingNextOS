using SingPlus.Contracts;
using SingPlus.Platform.HybridCpu;
using SingPlus.Sip;
using SingPlus.Runtime;
using System.Reflection;
using YAKSys_Hybrid_CPU.Core;
using System.Collections;

namespace SingPlus.Tests.Platform;

public sealed partial class PlatformDsc1ComputeTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MalformedProviderHistoryRetainsOriginalKernelChargeUntilExactHistoryRestored(bool lostHistory)
    {
        HybridCpuPlatformAuthorityProvider provider =new HybridCpuPlatformAuthorityProvider(); BudgetAccountHandle budget=default;
        Scenario scenario =CreateScenario(provider,2571,2572,32,(kernel,subject)=>
        {
            ProcessHandle admin =TestFixtures.Create(kernel,2573,2574).Handle;
            CapabilityId cap =kernel.MintCapability(new(2574),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
            budget=kernel.AdmitProcessBudget(admin,cap,subject,"malformed-dsc1-history",[new(ServiceBudgetDimension.OwnedMemoryBytes,128)]).Value!.ProcessBudget;
        },originalSlices:true);
        ProcessHandle foreign =TestFixtures.Create(scenario.Kernel,2575,2576).Handle;
        OwnedBuffer<byte> independent =scenario.Kernel.AllocateBuffer<byte>(foreign,16).Value!;
        scenario.Source.Span.Fill(0x29); PlatformDsc1CopySubmission submission =SubmitWhole(scenario).Value;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        IDictionary mappings =(global::System.Collections.IDictionary)provider.GetType().GetField("_providerMappings",flags)!.GetValue(provider)!;
        object record =mappings.Values.Cast<object>().First(m=>((global::System.Collections.IList)m.GetType().GetProperty("Dsc1Copies")!.GetValue(m)!).Count!=0);
        IList history =(global::System.Collections.IList)record.GetType().GetProperty("Dsc1Copies")!.GetValue(record)!;
        PropertyInfo issued =record.GetType().GetProperty("Dsc1CopiesIssued")!; ulong count =(ulong)issued.GetValue(record)!;
        object[] originals =history.Cast<object>().ToArray();
        if(lostHistory){history.Clear();issued.SetValue(record,0UL);}
        else {history.Add(null); issued.SetValue(record,count+1);}
        Assert.False(scenario.Kernel.ObservePlatformDsc1Copy(scenario.Subject,submission).IsSuccess);
        Assert.Equal(128UL,scenario.Kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Throws<InvalidOperationException>(()=>{_=scenario.Destination.Span.Length;});
        Assert.Equal(16,independent.Span.Length);
        history.Clear(); foreach(object? original in originals)history.Add(original); issued.SetValue(record,count);
        Assert.True(scenario.Kernel.ObservePlatformDsc1Copy(scenario.Subject,submission).IsSuccess);
        Assert.All(scenario.Destination.Span.ToArray(),b=>Assert.Equal((byte)0x29,b));
        Assert.True(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
        Assert.Equal(0UL,scenario.Kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(16,independent.Span.Length);
    }
    [Theory]
    [InlineData(96UL, false)]
    [InlineData(128UL, false)]
    [InlineData(128UL, true)]
    public void ActualKernelAdoptsOriginalSoftwareByteProducerWithChargedDisjointOutput(ulong limit, bool discard)
    {
        NeutralDomainRuntimeFacade native = new NeutralDomainRuntimeFacade();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(native);
        BudgetAccountHandle budget = default;
        Scenario scenario = CreateScenario(provider, 2531, 2532, 32, (kernel, subject) =>
        {
            ProcessHandle admin = TestFixtures.Create(kernel, 2533, 2534).Handle;
            CapabilityId cap = kernel.MintCapability(new(2534), admin, ResourceKind.KernelService,
                CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
            budget = kernel.AdmitProcessBudget(admin, cap, subject, "software-dsc1-backing",
                [new(ServiceBudgetDimension.OwnedMemoryBytes, limit)]).Value!.ProcessBudget;
        }, originalSlices: true);
        ulong Used() => scenario.Kernel.QueryBudget(budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used;
        scenario.Source.Span.Fill(0xA5);
        KernelResult<PlatformDsc1CopySubmission> submitted = SubmitWhole(scenario);
        if (limit == 96)
        {
            Assert.Equal(KernelError.BudgetExceeded, submitted.Error);
            Assert.Equal(64UL, Used());
            Assert.All(scenario.Destination.Span.ToArray(), b => Assert.Equal((byte)0, b));
        }
        else
        {
            Assert.True(submitted.IsSuccess, submitted.Message);
            Assert.Equal(128UL, Used());
            Assert.Throws<InvalidOperationException>(() => { _ = scenario.Destination.Span.Length; });
            KernelResult<PlatformDsc1CopyReceipt> receipt = discard ? scenario.Kernel.CancelPlatformDsc1Copy(scenario.Subject, submitted.Value) :
                scenario.Kernel.ObservePlatformDsc1Copy(scenario.Subject, submitted.Value);
            Assert.True(receipt.IsSuccess, receipt.Message);
            // Synchronous software work is already Completed; cancellation is
            // an original-result observation, not reversal of its byte effect.
            Assert.True(receipt.Value.OutputPublished);
            Assert.All(scenario.Destination.Span.ToArray(), b => Assert.Equal((byte)0xA5, b));
            Assert.Equal(64UL, Used());
            Assert.False(scenario.Kernel.ObservePlatformDsc1Copy(scenario.Subject, submitted.Value).IsSuccess);
        }
        Assert.True(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
        Assert.Equal(0UL, Used());
        Assert.Equal(0, native.ActiveOwnedRegionMappingCount);
        Assert.Equal(0, native.ActiveBindingCount);
        Assert.All(scenario.Kernel.Regions.Snapshot(), r => Assert.Equal(RegionState.Released, r.State));
    }

    public interface IOriginalDsc1Runtime : INeutralDomainRuntime, INeutralOriginalDomainCloseProvider,
        INeutralOriginalMappingClosureProvider, INeutralOriginalDsc1PreparationProvider { }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopiedOriginalOwnerAtEntryCannotConsumeBackingCharge(bool operationHistory)
    {
        BudgetAccountHandle budget = default;
        Scenario scenario = CreateScenario(new HybridCpuPlatformAuthorityProvider(), 2551, 2552, 32, (kernel, subject) =>
        {
            ProcessHandle admin = TestFixtures.Create(kernel, 2553, 2554).Handle;
            CapabilityId cap = kernel.MintCapability(new(2554), admin, ResourceKind.KernelService,
                CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
            budget = kernel.AdmitProcessBudget(admin, cap, subject, "original-dsc1-entry",
                [new(ServiceBudgetDimension.OwnedMemoryBytes, 128)]).Value!.ProcessBudget;
        }, originalSlices: true);
        scenario.Source.Span.Fill(0x7D);
        PlatformDsc1CopySubmission submission = SubmitWhole(scenario).Value;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        global::System.Collections.IDictionary Routes(object owner, string field) =>
            (global::System.Collections.IDictionary)owner.GetType().GetField(field, flags)!.GetValue(owner)!;
        IDictionary routes = operationHistory ? Routes(scenario.Kernel.PlatformAuthority, "_dsc1Operations") :
            Routes(scenario.Kernel, "_dsc1PayloadOperations");
        object original = routes[submission.SubmissionId]!;
        object copy = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        global::System.Collections.IList? history = null;
        int index = 0;
        if (operationHistory)
        {
            object domain = original.GetType().GetProperty("OriginalDomain")!.GetValue(original)!;
            history = (global::System.Collections.IList)domain.GetType().GetProperty("Dsc1Operations")!.GetValue(domain)!;
            index = history.IndexOf(original); history[index] = copy;
        }
        routes[submission.SubmissionId] = copy;
        Assert.False(scenario.Kernel.ObservePlatformDsc1Copy(scenario.Subject, submission).IsSuccess);
        Assert.Equal(128UL, scenario.Kernel.QueryBudget(budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Throws<InvalidOperationException>(() => { _ = scenario.Destination.Span.Length; });
        routes[submission.SubmissionId] = original;
        if (history is not null) history[index] = original;
        Assert.True(scenario.Kernel.ObservePlatformDsc1Copy(scenario.Subject, submission).IsSuccess);
        Assert.All(scenario.Destination.Span.ToArray(), b => Assert.Equal((byte)0x7D, b));
        Assert.True(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
        Assert.Equal(0UL, scenario.Kernel.QueryBudget(budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    public class Dsc1NativeSpy : DispatchProxy
    {
        public NeutralDomainRuntimeFacade Native = null!;
        public bool LosePrepareReply, LoseCopyReply, LoseQuery;
        public bool CopyRaw;
        private object? copiedRaw;
        public bool LoseSourceAfterCopy, SourceUnavailable;
        public Action? AfterCopy;
        public Action? AfterQuery;
        public int Prepares, Copies;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_MappingClosureIncarnation" && SourceUnavailable) return Guid.Empty;
            if (method!.Name == nameof(INeutralOriginalDsc1CopyProvider.ResolveDsc1Copy) && LoseQuery) return null;
            object? result = method.Invoke(Native, args);
            if (method.Name == nameof(INeutralOriginalDsc1CopyProvider.ResolveDsc1Copy)) AfterQuery?.Invoke();
            if (CopyRaw && method.Name == nameof(INeutralOriginalDsc1CopyProvider.ResolveDsc1Copy) && result is not null)
                return copiedRaw ??= typeof(object).GetMethod("MemberwiseClone",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(result,null);
            if (method.Name == nameof(INeutralOriginalDsc1CopyProvider.PrepareDsc1Copy))
            {
                Prepares++;
                if (LosePrepareReply) throw new IOException("Lost native preparation reply");
            }
            if (method.Name == nameof(INeutralOriginalDsc1CopyProvider.CopyDsc1))
            {
                Copies++;
                AfterCopy?.Invoke();
                if (LoseSourceAfterCopy) SourceUnavailable = true;
                if (LoseCopyReply) throw new IOException("Lost original native copy reply");
            }
            return result;
        }
    }

    [Fact]
    public void LateNativeSourceLossRetains128ThenOriginalCleanupDiscardsUnpublishedOutput()
    {
        IOriginalDsc1Runtime proxy =DispatchProxy.Create<IOriginalDsc1Runtime,Dsc1NativeSpy>();
        Dsc1NativeSpy spy =(Dsc1NativeSpy)(object)proxy; spy.Native=new(); spy.LoseSourceAfterCopy=true;
        BudgetAccountHandle budget=default;
        Scenario scenario =CreateScenario(new HybridCpuPlatformAuthorityProvider(proxy),2561,2562,32,(kernel,subject)=>
        {
            ProcessHandle admin =TestFixtures.Create(kernel,2563,2564).Handle;
            CapabilityId cap =kernel.MintCapability(new(2564),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
            budget=kernel.AdmitProcessBudget(admin,cap,subject,"late-dsc1-source",[new(ServiceBudgetDimension.OwnedMemoryBytes,128)]).Value!.ProcessBudget;
        },originalSlices:true);
        ProcessHandle foreign =TestFixtures.Create(scenario.Kernel,2565,2566).Handle;
        OwnedBuffer<byte> independent =scenario.Kernel.AllocateBuffer<byte>(foreign,16).Value!;
        scenario.Source.Span.Fill(0xC3);
        Assert.False(SubmitWhole(scenario).IsSuccess); Assert.Equal(1,spy.Copies);
        ulong Used()=>scenario.Kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        Assert.Equal(128UL,Used());
        Assert.False(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess); Assert.Equal(128UL,Used());
        spy.SourceUnavailable=false;
        Assert.True(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
        Assert.Equal(1,spy.Copies); Assert.Equal(0UL,Used());
        Assert.Equal(0,spy.Native.ActiveOwnedRegionMappingCount); Assert.Equal(0,spy.Native.ActiveBindingCount);
        Assert.Equal(16,independent.Span.Length);
        Assert.Equal(RegionState.Owned,scenario.Kernel.Regions.Snapshot().Single(r=>r.Handle==independent.Handle).State);
    }

    [Theory]
    [InlineData(false,false)]
    [InlineData(true,false)]
    [InlineData(false,true)]
    public void ActualKernelUnknownCopyRetains128AndReconcilesSameOriginalWithoutRepeat(bool lostPreparation, bool copiedRaw)
    {
        IOriginalDsc1Runtime proxy = DispatchProxy.Create<IOriginalDsc1Runtime, Dsc1NativeSpy>();
        Dsc1NativeSpy spy = (Dsc1NativeSpy)(object)proxy;
        spy.Native = new(); spy.LosePrepareReply = lostPreparation; spy.LoseCopyReply = true; spy.LoseQuery = !copiedRaw; spy.CopyRaw=copiedRaw;
        BudgetAccountHandle budget = default;
        Scenario scenario = CreateScenario(new HybridCpuPlatformAuthorityProvider(proxy), 2541, 2542, 32, (kernel, subject) =>
        {
            ProcessHandle admin = TestFixtures.Create(kernel, 2543, 2544).Handle;
            CapabilityId cap = kernel.MintCapability(new(2544), admin, ResourceKind.KernelService,
                CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
            budget = kernel.AdmitProcessBudget(admin, cap, subject, "unknown-dsc1-backing",
                [new(ServiceBudgetDimension.OwnedMemoryBytes, 128)]).Value!.ProcessBudget;
        }, originalSlices: true);
        ProcessHandle foreign = TestFixtures.Create(scenario.Kernel, 2545, 2546).Handle;
        OwnedBuffer<byte> independent = scenario.Kernel.AllocateBuffer<byte>(foreign, 16).Value!;
        scenario.Source.Span.Fill(0x6B);
        Assert.False(SubmitWhole(scenario).IsSuccess);
        Assert.Equal(1, spy.Prepares); Assert.Equal(lostPreparation ? 0 : 1, spy.Copies);
        ulong Used() => scenario.Kernel.QueryBudget(budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used;
        Assert.Equal(128UL, Used());
        Assert.False(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
        Assert.Equal(128UL, Used()); Assert.Equal(lostPreparation ? 0 : 1, spy.Copies);
        Assert.Equal(16, independent.Span.Length);
        spy.LoseQuery = false;
        spy.CopyRaw = false;
        if (lostPreparation)
        {
            Assert.True(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
            Assert.Equal(1, spy.Prepares); Assert.Equal(0, spy.Copies); Assert.Equal(0UL, Used());
            Assert.Equal(0, spy.Native.ActiveOwnedRegionMappingCount);
            Assert.Equal(0, spy.Native.ActiveBindingCount);
            Assert.Equal(16, independent.Span.Length);
            return;
        }
        Assert.True(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
        Assert.Equal(1, spy.Copies); Assert.Equal(0UL, Used());
        Assert.Equal(0, spy.Native.ActiveOwnedRegionMappingCount);
        Assert.Equal(0, spy.Native.ActiveBindingCount);
        Assert.Equal(16, independent.Span.Length);
        Assert.Equal(RegionState.Owned, scenario.Kernel.Regions.Snapshot().Single(r => r.Handle == independent.Handle).State);
    }
}
