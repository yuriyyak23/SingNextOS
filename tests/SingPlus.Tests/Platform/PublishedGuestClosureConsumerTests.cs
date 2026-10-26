using System.Collections;
using System.Reflection;
using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Tests.Platform;

public sealed class PublishedGuestClosureConsumerTests
{
    [Theory]
    [InlineData(false, "duplicate", false)]
    [InlineData(false, "copy", false)]
    [InlineData(false, "request", false)]
    [InlineData(false, "native-request", false)]
    [InlineData(true, "duplicate", false)]
    [InlineData(true, "copy", false)]
    [InlineData(true, "request", false)]
    [InlineData(true, "native-request", false)]
    [InlineData(false, "duplicate", true)]
    [InlineData(false, "copy", true)]
    [InlineData(false, "request", true)]
    [InlineData(false, "native-request", true)]
    [InlineData(true, "duplicate", true)]
    [InlineData(true, "copy", true)]
    [InlineData(true, "request", true)]
    [InlineData(true, "native-request", true)]
    public void CompletedSiblingHistoryCannotAuthorizeAnotherNestedEffect(bool sdkHistory, string fault, bool lostReply)
        => RunCompletedSiblingFault(sdkHistory, fault, lostReply);

    [Theory]
    [InlineData(true, "raw-loss", false)]
    [InlineData(true, "raw-copy", false)]
    [InlineData(true, "canonical-loss", false)]
    [InlineData(true, "canonical-copy", false)]
    [InlineData(true, "close-loss", false)]
    [InlineData(true, "close-copy", false)]
    [InlineData(false, "raw-loss", false)]
    [InlineData(false, "close-loss", false)]
    [InlineData(false, "close-copy", false)]
    [InlineData(true, "raw-loss", true)]
    [InlineData(true, "raw-copy", true)]
    [InlineData(true, "canonical-loss", true)]
    [InlineData(true, "canonical-copy", true)]
    [InlineData(true, "raw-and-canonical-copy", false)]
    [InlineData(true, "raw-and-canonical-copy", true)]
    [InlineData(true, "close-loss", true)]
    [InlineData(true, "close-copy", true)]
    [InlineData(false, "raw-loss", true)]
    [InlineData(false, "close-loss", true)]
    [InlineData(false, "close-copy", true)]
    public void CompletedSiblingCanonicalCustodyCannotAuthorizeAnotherNestedEffect(bool sdkHistory, string fault, bool lostReply)
        => RunCompletedSiblingFault(sdkHistory, fault, lostReply);

    [Theory]
    [InlineData(true, "raw-loss", false)]
    [InlineData(true, "close-copy", false)]
    [InlineData(false, "raw-loss", false)]
    [InlineData(false, "close-copy", false)]
    [InlineData(true, "raw-loss", true)]
    [InlineData(true, "close-copy", true)]
    [InlineData(false, "raw-loss", true)]
    [InlineData(false, "close-copy", true)]
    public void CompletedSiblingCallbackCustodyRetainsNewOriginalAfterEffect(bool sdkHistory, string fault, bool lostReply)
        => RunCompletedSiblingFault(sdkHistory, fault, lostReply, afterEffect: true);

    [Theory]
    [InlineData("raw-loss", false, false)] [InlineData("raw-copy", false, false)]
    [InlineData("closure-loss", false, false)] [InlineData("closure-copy", false, false)]
    [InlineData("close-loss", false, false)] [InlineData("close-copy", false, false)]
    [InlineData("raw-loss", true, false)] [InlineData("raw-copy", true, false)]
    [InlineData("closure-loss", true, false)] [InlineData("closure-copy", true, false)]
    [InlineData("close-loss", true, false)] [InlineData("close-copy", true, false)]
    [InlineData("raw-copy", false, true)] [InlineData("close-copy", false, true)]
    [InlineData("raw-copy", true, true)] [InlineData("close-copy", true, true)]
    public void NativeCompletedSiblingCustodyRetainsBackingUntilExactRepair(string fault, bool lostReply, bool afterEffect)
        => RunCompletedSiblingFault(false, fault, lostReply, afterEffect, nativeHistory: true);

    [Theory]
    [InlineData("raw-loss", false, false)] [InlineData("raw-copy", false, false)]
    [InlineData("request-copy", false, false)] [InlineData("coordinated-copy", false, false)]
    [InlineData("raw-loss", true, false)] [InlineData("raw-copy", true, false)]
    [InlineData("request-copy", true, false)] [InlineData("coordinated-copy", true, false)]
    [InlineData("raw-loss", false, true)] [InlineData("raw-copy", false, true)]
    [InlineData("request-copy", false, true)] [InlineData("coordinated-copy", false, true)]
    [InlineData("raw-loss", true, true)] [InlineData("raw-copy", true, true)]
    [InlineData("request-copy", true, true)] [InlineData("coordinated-copy", true, true)]
    public void NativeOrdinaryFactoryCustodyGatesNestedEffectAndRootReclaim(string fault, bool lostReply, bool afterEffect)
        => RunCompletedSiblingFault(false, fault, lostReply, afterEffect, nativeHistory: true, rootFactory: true);

    private static void RunCompletedSiblingFault(bool sdkHistory, string fault, bool lostReply, bool afterEffect = false, bool nativeHistory = false, bool rootFactory = false)
    {
        var native = DispatchProxy.Create<ExactNestedDomainConsumerTests.INestedRuntime, ExactNestedDomainConsumerTests.NestedSpy>();
        var spy = (ExactNestedDomainConsumerTests.NestedSpy)(object)native;
        HybridCpuExecutableChildAdapter sdk = new(spy.Inner, native);
        HybridCpuPlatformAuthorityProvider provider = new(new NeutralDomainRuntimeFacade(), sdk);
        var route = DispatchProxy.Create<INestedReplyProvider, NestedHistoryReplySpy>();
        var replies = (NestedHistoryReplySpy)(object)route; replies.Inner = provider;
        RuntimeKernel kernel = new(lostReply ? route : provider);
        var (process, owner) = TestFixtures.Create(kernel, 3875, 38750);
        ProcessHandle admin = TestFixtures.Create(kernel, 3876, 38760).Handle;
        CapabilityId budgetCap = kernel.MintCapability(new(38760), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "nested-inventory",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        OwnedBuffer<byte> region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        OwnedBuffer<byte> foreign = kernel.AllocateBuffer<byte>(admin, 16).Value!; foreign.Span.Fill(0x73);
        CapabilityId create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var root = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, root.Domain, root.ConfigureCapability).IsSuccess);
        for (int index = 0; index < 2; index++)
        {
            var child = kernel.CreateNestedVirtualDomain(owner, root.Domain, root.ConfigureCapability,
                new(new(1, 1024), VirtualDomainAuthorityClass.Lifecycle));
            Assert.True(child.IsSuccess, child.Message);
            Assert.True(kernel.DestroyVirtualDomain(owner, child.Value!.Domain, child.Value.ConfigureCapability).IsSuccess);
        }
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        object holder = nativeHistory ? spy.Inner : sdkHistory ? sdk : provider;
        IDictionary children = (IDictionary)holder.GetType().GetField(nativeHistory ? "childRegistry" : sdkHistory ? "originalChildren" : "_originalProviderChildren", hidden)!.GetValue(holder)!;
        object parent = children.Values.Cast<object>().Single(x => x.GetType().GetProperty(nativeHistory ? "ImmediateParent" : "NestedParent")!.GetValue(x) is null);
        object? nativeRoot = rootFactory ? parent.GetType().GetProperty("ParentSource")!.GetValue(parent)! : null;
        IList history = rootFactory ? (IList)nativeRoot!.GetType().GetProperty("OriginalChildCreates")!.GetValue(nativeRoot)! :
            (IList)parent.GetType().GetProperty(nativeHistory ? "OriginalNestedChildren" : "OriginalNested")!.GetValue(parent)!;
        Assert.Equal(rootFactory ? 1 : 2, history.Count);
        object first = history[0]!, second = history[rootFactory ? 0 : 1]!;
        Action inject, restore;
        if (rootFactory && (fault is "request-copy" or "coordinated-copy"))
        {
            FieldInfo requestSlot = second.GetType().GetField("<Request>k__BackingField", hidden)!;
            var request = (ExternalOriginalChildCreateRequest)requestSlot.GetValue(second)!;
            PropertyInfo observationSlot = second.GetType().GetProperty("Observation")!;
            var observation = (ExternalOriginalChildCreateObservation)observationSlot.GetValue(second)!;
            var copy = request with { };
            inject = () => { requestSlot.SetValue(second, copy); if (fault == "coordinated-copy") observationSlot.SetValue(second, observation with { Request = copy }); };
            restore = () => { requestSlot.SetValue(second, request); observationSlot.SetValue(second, observation); };
        }
        else if (fault is "duplicate" or "copy")
        {
            inject = () => history[1] = fault == "duplicate" ? first : typeof(object).GetMethod("MemberwiseClone", hidden)!.Invoke(second, null);
            restore = () => history[1] = second;
        }
        else if (fault is "request" or "native-request")
        {
            FieldInfo source = second.GetType().GetField(fault == "request" ? "<Request>k__BackingField" : "<NativeRequest>k__BackingField", hidden)!;
            object original = source.GetValue(second)!;
            inject = () => source.SetValue(second, typeof(object).GetMethod("MemberwiseClone", hidden)!.Invoke(original, null));
            restore = () => source.SetValue(second, original);
        }
        else if (fault == "raw-and-canonical-copy")
        {
            FieldInfo raw = second.GetType().GetField("Raw")!, canonical = second.GetType().GetField("Canonical")!;
            object original = raw.GetValue(second)!;
            Assert.Same(original, canonical.GetValue(second));
            object copy = typeof(object).GetMethod("MemberwiseClone", hidden)!.Invoke(original, null)!;
            inject = () => { raw.SetValue(second, copy); canonical.SetValue(second, copy); };
            restore = () => { raw.SetValue(second, original); canonical.SetValue(second, original); };
        }
        else
        {
            object target = second;
            if (fault.StartsWith("close-", StringComparison.Ordinal) || fault.StartsWith("closure-", StringComparison.Ordinal))
                target = nativeHistory ? second.GetType().GetProperty("Child")!.GetValue(second)! : second.GetType().GetField("Child")!.GetValue(second)!;
            MemberInfo member = nativeHistory ? target.GetType().GetProperty(fault.StartsWith("raw-", StringComparison.Ordinal) ? "Observation" : fault.StartsWith("closure-", StringComparison.Ordinal) ? "OriginalClosure" : "OriginalClose")! : fault.StartsWith("close-", StringComparison.Ordinal)
                ? target.GetType().GetProperty("OriginalCloseInvocation")!
                : target.GetType().GetField(fault.StartsWith("raw-", StringComparison.Ordinal) ? "Raw" : "Canonical")!;
            object original = member is FieldInfo field ? field.GetValue(target)! : ((PropertyInfo)member).GetValue(target)!;
            Assert.NotNull(original);
            void Set(object? value)
            { if (member is FieldInfo field) field.SetValue(target, value); else ((PropertyInfo)member).SetValue(target, value); }
            inject = () => Set(fault.EndsWith("copy", StringComparison.Ordinal) ? typeof(object).GetMethod("MemberwiseClone", hidden)!.Invoke(original, null) : null);
            restore = () => Set(original);
        }
        if (afterEffect)
            spy.After = name =>
            {
                if (name != "CreateOriginalNestedChild") return;
                spy.After = null;
                Assert.False(Monitor.IsEntered(typeof(HybridCpuExecutableChildAdapter).GetField("sync", hidden)!.GetValue(sdk)!));
                Assert.False(Monitor.IsEntered(typeof(HybridCpuPlatformAuthorityProvider).GetField("_childCreateCommitGate", hidden)!.GetValue(provider)!));
                if (rootFactory) Assert.False(Monitor.IsEntered(nativeRoot!.GetType().GetProperty("Sync")!.GetValue(nativeRoot)!));
                Assert.False(kernel.CreateNestedVirtualDomain(owner, root.Domain, root.ConfigureCapability,
                    new(new(1, 1024), VirtualDomainAuthorityClass.Lifecycle)).IsSuccess);
                inject();
            };
        else inject();
        replies.LoseNestedReply = lostReply; replies.NestedQueryLosses = lostReply ? 1 : 0;
        Assert.False(kernel.CreateNestedVirtualDomain(owner, root.Domain, root.ConfigureCapability,
            new(new(1, 1024), VirtualDomainAuthorityClass.Lifecycle)).IsSuccess);
        int creates = nativeHistory || afterEffect ? 3 : 2;
        Assert.Equal(creates, spy.Calls.GetValueOrDefault("CreateOriginalNestedChild"));
        Assert.Equal(afterEffect ? 3 : 2, spy.NestedEffects);
        if (!nativeHistory) Assert.Equal(2, spy.Calls.GetValueOrDefault("CloseOriginalChild"));
        Assert.Equal(2, spy.CloseEffects);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(creates, spy.Calls.GetValueOrDefault("CreateOriginalNestedChild"));
        if (!nativeHistory) Assert.Equal(2, spy.Calls.GetValueOrDefault("CloseOriginalChild"));
        Assert.Equal(2, spy.CloseEffects);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        if (rootFactory) Assert.NotEqual("Closed", nativeRoot!.GetType().GetProperty("Lifecycle")!.GetValue(nativeRoot)!.ToString());
        restore();
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(creates, spy.Calls.GetValueOrDefault("CreateOriginalNestedChild"));
        if (!nativeHistory) Assert.Equal(creates + 1, spy.Calls.GetValueOrDefault("CloseOriginalChild"));
        Assert.Equal(afterEffect ? 4 : 3, spy.CloseEffects);
        if (nativeHistory) Assert.Equal(afterEffect ? 4 : 3, children.Count);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage, x => Assert.Equal(0UL, x.Used));
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        if (rootFactory) Assert.Equal("Closed", nativeRoot!.GetType().GetProperty("Lifecycle")!.GetValue(nativeRoot)!.ToString());
        Assert.All(foreign.Span.ToArray(), x => Assert.Equal((byte)0x73, x));
        if (lostReply) Assert.Equal(3, replies.NestedCreates);
    }
    public class NestedHistoryReplySpy : OriginalLoanRefusalConsumerTests.DeviceReplySpy
    {
        public bool LoseNestedReply;
        public int NestedQueryLosses, NestedCreates;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "ResolveOriginalNestedDomain" && NestedQueryLosses > 0)
            { NestedQueryLosses--; throw new IOException("Original nested refusal query lost."); }
            if (method.Name == "CreateOriginalNestedDomain") NestedCreates++;
            object? result = base.Invoke(method, args);
            if (method.Name == "CreateOriginalNestedDomain" && LoseNestedReply)
            { LoseNestedReply = false; throw new IOException("Original nested refusal reply lost."); }
            return result;
        }
    }
    public interface INestedReplyProvider : IGuestReplyProvider, IPlatformNestedDomainProvider, IPlatformOriginalNestedDomainProvider, IPlatformOriginalChildDrainProvider { }
    public class DrainReplySpy : OriginalLoanRefusalConsumerTests.DeviceReplySpy
    {
        public bool LoseDrainReply;
        public int QueryLosses;
        public bool CopyProof, ForgeNoEffect;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name is "ResolveOriginalChildDrain" or "ObserveOriginalChildDrain" && QueryLosses > 0)
            { QueryLosses--; throw new IOException("Platform original drain query lost."); }
            object? raw = base.Invoke(method, args);
            if (method.Name == "ObserveOriginalChildDrain" && CopyProof && raw is PlatformOriginalChildDrainObservation proof) return proof with { };
            if (method.Name == "BeginOriginalChildDrain" && ForgeNoEffect && raw is PlatformAuthorityResult { IsSuccess: true })
            { ForgeNoEffect = false; return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted, "Forged no-effect after actual drain."); }
            if (method.Name == "BeginOriginalChildDrain" && LoseDrainReply && raw is PlatformAuthorityResult { IsSuccess: true })
            { LoseDrainReply = false; throw new IOException("Platform drain reply lost after publication."); }
            return raw;
        }
    }
    public interface ITransitionProbe : INeutralChildDomainProvider, INeutralOriginalChildCreateProvider,
        INeutralOriginalExternalParentBindNoEffectProvider, INeutralOriginalNestedDomainProvider,
        INeutralExternalParentClosureProvider, INeutralOriginalChildCloseProvider, INeutralRuntimeFeatureProvider, INeutralOriginalChildDrainProvider { }
    public class TransitionProbe : DispatchProxy
    {
        public HybridCpuExecutableChildAdapter Inner = null!;
        public Action? AfterTransition;
        public int TransitionCalls;
        public int DrainQueries, QueryLosses;
        public bool LoseReply, CopyReply;
        public NeutralOriginalChildDrainRequest? OriginalDrain;
        public Dictionary<NeutralOriginalChildDrainRequest, int> DrainInvocations = new(ReferenceEqualityComparer.Instance);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            object? result;
            if (method!.Name is "TransitionChildDomain" or "BeginOriginalChildDrain") TransitionCalls++;
            if (method.Name == "ResolveOriginalChildDrain")
            { DrainQueries++; if (QueryLosses-- > 0) throw new IOException("Original drain query lost."); }
            try { result = method!.Invoke(Inner, args); }
            catch (TargetInvocationException exception) { throw exception.InnerException!; }
            if (method!.Name == "TransitionChildDomain" && result is NeutralVirtualizationResult { IsSuccess: true }) AfterTransition?.Invoke();
            if (method.Name == "BeginOriginalChildDrain")
            {
                OriginalDrain = (NeutralOriginalChildDrainRequest)args![0]!;
                DrainInvocations[OriginalDrain] = DrainInvocations.GetValueOrDefault(OriginalDrain) + 1;
                AfterTransition?.Invoke();
                if (LoseReply) throw new IOException("Original drain reply lost after local effect.");
                if (CopyReply && result is NeutralOriginalChildDrainObservation raw) return raw with { };
            }
            return result;
        }
    }
    [Theory]
    [InlineData("FactoryLoss")]
    [InlineData("FactoryCopy")]
    [InlineData("HistoryLoss")]
    [InlineData("RawLoss")]
    [InlineData("ParentLoss")]
    [InlineData("PreFactoryLoss")]
    [InlineData("PreFactoryCopy")]
    [InlineData("PreHistoryLoss")]
    [InlineData("PreRawLoss")]
    [InlineData("PreParentLoss")]
    [InlineData("TransitionCallbackLoss")]
    [InlineData("TransitionCallbackReplyLoss")]
    [InlineData("TransitionCallbackQueryLoss")]
    [InlineData("TransitionCallbackCopy")]
    [InlineData("TransitionCallbackSdkRawLoss")]
    [InlineData("TransitionCallbackSdkRawCopy")]
    [InlineData("TransitionCallbackProviderRawLoss")]
    [InlineData("TransitionCallbackProviderRawCopy")]
    [InlineData("TransitionCallbackRevision")]
    [InlineData("TransitionCallbackRequestLoss")]
    [InlineData("TransitionCallbackRequestCopy")]
    [InlineData("TransitionCallbackProviderReplyLoss")]
    [InlineData("TransitionCallbackProofCopy")]
    [InlineData("TransitionCallbackForgedNoEffect")]
    public void PublishedRootRetainsBackingUntilOriginalNestedFactoryIsPresent(string fault)
    {
        var native = DispatchProxy.Create<ExactNestedDomainConsumerTests.INestedRuntime, ExactNestedDomainConsumerTests.NestedSpy>();
        var nativeSpy = (ExactNestedDomainConsumerTests.NestedSpy)(object)native;
        HybridCpuExecutableChildAdapter sdk = new(nativeSpy.Inner, native);
        ITransitionProbe transitionProbe = DispatchProxy.Create<ITransitionProbe, TransitionProbe>();
        var probe = (TransitionProbe)(object)transitionProbe; probe.Inner = sdk;
        HybridCpuPlatformAuthorityProvider provider = new(new NeutralDomainRuntimeFacade(), fault.StartsWith("TransitionCallback", StringComparison.Ordinal) ? transitionProbe : sdk);
        var proxy = DispatchProxy.Create<INestedReplyProvider, DrainReplySpy>();
        var spy = (DrainReplySpy)(object)proxy;
        spy.Inner = provider; spy.LoseReply = false; spy.LoseRootReply = true;
        RuntimeKernel kernel = new(proxy);
        var (process, owner) = TestFixtures.Create(kernel, 3873, 38730);
        ProcessHandle admin = TestFixtures.Create(kernel, 3874, 38740).Handle;
        CapabilityId budgetCap = kernel.MintCapability(new(38740), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "nested-custody",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        OwnedBuffer<byte> region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        OwnedBuffer<byte> foreign = kernel.AllocateBuffer<byte>(admin, 16).Value!; foreign.Span.Fill(0x73);
        CapabilityId create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var root = kernel.CreateVirtualDomain(owner, create, new(1, 4096));
        Assert.True(root.IsSuccess, root.Message);
        Assert.True(kernel.ConfigureVirtualDomain(owner, root.Value!.Domain, root.Value.ConfigureCapability).IsSuccess);
        var nested = kernel.CreateNestedVirtualDomain(owner, root.Value.Domain, root.Value.ConfigureCapability,
            new(new(1, 1024), VirtualDomainAuthorityClass.Lifecycle));
        Assert.True(nested.IsSuccess, nested.Message);
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary children = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderChildren", hidden)!.GetValue(provider)!;
        object child = children.Values.Cast<object>().Single(x => x.GetType().GetProperty("NestedParent")!.GetValue(x) is not null);
        object parent = child.GetType().GetProperty("NestedParent")!.GetValue(child)!;
        IList history = (IList)parent.GetType().GetProperty("OriginalNested")!.GetValue(parent)!;
        object factory = Assert.Single(history.Cast<object>());
        PropertyInfo route = child.GetType().GetProperty("NestedFactory")!;
        FieldInfo raw = factory.GetType().GetField("Raw")!;
        object? retainedRaw = raw.GetValue(factory);
        if (fault.StartsWith("TransitionCallback", StringComparison.Ordinal))
        {
            spy.LoseRootReply = false;
            probe.LoseReply = fault == "TransitionCallbackReplyLoss";
            probe.CopyReply = fault == "TransitionCallbackCopy";
            probe.QueryLosses = fault == "TransitionCallbackQueryLoss" ? 1 : 0;
            spy.LoseDrainReply = fault == "TransitionCallbackProviderReplyLoss";
            spy.QueryLosses = spy.LoseDrainReply ? 1 : 0;
            spy.ForgeNoEffect = fault == "TransitionCallbackForgedNoEffect";
            var childLease = (PlatformProviderChildDomainLease)child.GetType().GetProperty("Lease")!.GetValue(child)!;
            object commitGate = typeof(HybridCpuPlatformAuthorityProvider).GetField("_childCreateCommitGate", hidden)!.GetValue(provider)!;
            object? sdkDrainFact = null;
            object? savedSdkRaw = null;
            probe.AfterTransition = () =>
            {
                Assert.False(Monitor.IsEntered(commitGate));
                Assert.False(provider.TransitionChildDomain(childLease, PlatformChildDomainTransition.BeginDrain).IsSuccess);
                Assert.Equal(PlatformAuthorityStatus.NotAccepted, provider.BeginOriginalChildDrain(new(childLease)).Status);
                IDictionary sdkDrains = (IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("originalDrains", hidden)!.GetValue(sdk)!;
                sdkDrainFact = sdkDrains[probe.OriginalDrain!]!;
                savedSdkRaw = sdkDrainFact.GetType().GetField("Raw")!.GetValue(sdkDrainFact);
                object sdkOwner = sdkDrainFact.GetType().GetProperty("Child")!.GetValue(sdkDrainFact)!;
                object? beforeRevision = sdkOwner.GetType().GetProperty("StateRevision")!.GetValue(sdkOwner);
                NeutralOriginalChildDrainRequest overlapping = new(probe.OriginalDrain!.Child);
                NeutralOriginalChildDrainObservation? refused = sdk.BeginOriginalChildDrain(overlapping);
                Assert.NotNull(refused);
                Assert.Equal(NeutralVirtualizationStatus.NotInvoked, refused.Result.Status);
                Assert.Same(refused, sdk.ResolveOriginalChildDrain(overlapping));
                Assert.Null(sdk.BeginOriginalChildDrain(probe.OriginalDrain));
                Assert.True(sdk.TransitionChildDomain(probe.OriginalDrain.Child, NeutralChildDomainTransition.BeginDrain).IsSuccess);
                Assert.Equal(beforeRevision, sdkOwner.GetType().GetProperty("StateRevision")!.GetValue(sdkOwner));
                if (fault is "TransitionCallbackSdkRawLoss" or "TransitionCallbackSdkRawCopy")
                    sdkDrainFact.GetType().GetField("Raw")!.SetValue(sdkDrainFact,
                        fault == "TransitionCallbackSdkRawCopy" ? ((NeutralOriginalChildDrainObservation)savedSdkRaw!) with { } : null);
                if (fault is not ("TransitionCallbackProviderReplyLoss" or "TransitionCallbackForgedNoEffect")) route.SetValue(child, null);
            };
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            if (fault == "TransitionCallbackForgedNoEffect") spy.QueryLosses = 1;
            probe.AfterTransition = null;
            probe.LoseReply = false; probe.CopyReply = false; probe.QueryLosses = 0;
            Assert.False(kernel.TerminateProcess(owner).IsSuccess); // Source still missing: query is not fresh admission.
            Assert.NotNull(probe.OriginalDrain);
            Assert.Null(sdk.ResolveOriginalChildDrain(probe.OriginalDrain! with { }));
            Assert.Null(new HybridCpuExecutableChildAdapter(nativeSpy.Inner, native).ResolveOriginalChildDrain(probe.OriginalDrain!));
            route.SetValue(child, factory);
            object? failure = child.GetType().GetProperty("FailedTransition")!.GetValue(child);
            if (fault is not ("TransitionCallbackProviderReplyLoss" or "TransitionCallbackForgedNoEffect")) Assert.NotNull(failure);
            if (fault == "TransitionCallbackLoss") Assert.NotNull(failure.GetType().GetProperty("Observation")!.GetValue(failure));
            IDictionary providerDrains = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalChildDrains", hidden)!.GetValue(provider)!;
            object drain = providerDrains.Values.Cast<object>().Single(x => (bool)x.GetType().GetField("Invoked")!.GetValue(x)!);
            var platformRequest = (PlatformOriginalChildDrainRequest)drain.GetType().GetProperty("Request")!.GetValue(drain)!;
            Assert.False(provider.ResolveOriginalChildDrain(platformRequest with { }).IsSuccess);
            Assert.False(new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), sdk).ResolveOriginalChildDrain(platformRequest).IsSuccess);
            object? savedProviderRaw = drain.GetType().GetField("Raw")!.GetValue(drain);
            IDictionary bridgeChildren = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_originalChildOwners", hidden)!.GetValue(kernel.PlatformAuthority)!;
            object bridgeChild = bridgeChildren.Values.Cast<object>().Single(x => x.GetType().GetProperty("OriginalDrainRequest")!.GetValue(x) is not null);
            PropertyInfo requestRoute = bridgeChild.GetType().GetProperty("OriginalDrainRequest")!;
            var savedRequest = (PlatformOriginalChildDrainRequest)requestRoute.GetValue(bridgeChild)!;
            if (fault is "TransitionCallbackRequestLoss" or "TransitionCallbackRequestCopy")
                requestRoute.SetValue(bridgeChild, fault == "TransitionCallbackRequestCopy" ? savedRequest with { } : null);
            if (fault is "TransitionCallbackProviderRawLoss" or "TransitionCallbackProviderRawCopy")
                drain.GetType().GetField("Raw")!.SetValue(drain, fault == "TransitionCallbackProviderRawCopy"
                    ? ((NeutralOriginalChildDrainObservation)savedProviderRaw!) with { } : null);
            IDictionary sdkChildren = (IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("originalChildren", hidden)!.GetValue(sdk)!;
            object sdkChild = sdkChildren.Values.Cast<object>().Single(x => x.GetType().GetProperty("NestedParent")!.GetValue(x) is not null);
            FieldInfo revision = sdkChild.GetType().GetField("_stateRevision", hidden)!;
            object? savedRevision = revision.GetValue(sdkChild);
            if (fault == "TransitionCallbackRevision") revision.SetValue(sdkChild, (long)savedRevision! + 1);
            spy.CopyProof = fault == "TransitionCallbackProofCopy";
            if (fault is "TransitionCallbackSdkRawLoss" or "TransitionCallbackSdkRawCopy" or "TransitionCallbackProviderRawLoss" or "TransitionCallbackProviderRawCopy" or "TransitionCallbackRevision" or "TransitionCallbackRequestLoss" or "TransitionCallbackRequestCopy" or "TransitionCallbackProofCopy")
            {
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
                Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
                sdkDrainFact!.GetType().GetField("Raw")!.SetValue(sdkDrainFact, savedSdkRaw);
                drain.GetType().GetField("Raw")!.SetValue(drain, savedProviderRaw); revision.SetValue(sdkChild, savedRevision);
                requestRoute.SetValue(bridgeChild, savedRequest);
                spy.CopyProof = false;
            }
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(2, probe.TransitionCalls);
            Assert.All(probe.DrainInvocations.Values, count => Assert.Equal(1, count));
            Assert.True(probe.DrainQueries >= 1);
            Assert.Equal(2, nativeSpy.Calls.GetValueOrDefault("CloseOriginalChild"));
            Assert.Equal(1, spy.RootRevokes);
            Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.All(kernel.QueryBudget(budget).Value!.Usage, x => Assert.Equal(0UL, x.Used));
            Assert.All(foreign.Span.ToArray(), x => Assert.Equal((byte)0x73, x));
            return;
        }
        bool beforeEffect = fault.StartsWith("Pre", StringComparison.Ordinal);
        if (beforeEffect) fault = fault[3..];
        spy.AfterRootClose = () =>
        {
            if (!beforeEffect) Assert.True((bool)child.GetType().GetProperty("ClosurePublished")!.GetValue(child)!);
            if (fault == "HistoryLoss") history.Clear();
            else if (fault == "RawLoss") raw.SetValue(factory, null);
            else if (fault == "ParentLoss") child.GetType().GetProperty("NestedParent")!.SetValue(child, null);
            else route.SetValue(child, fault == "FactoryCopy" ? typeof(object).GetMethod("MemberwiseClone", hidden)!.Invoke(factory, null) : null);
        };
        void Restore()
        {
            history.Clear(); history.Add(factory); route.SetValue(child, factory); raw.SetValue(factory, retainedRaw);
            child.GetType().GetProperty("NestedParent")!.SetValue(child, parent); spy.AfterRootClose = null;
        }
        if (beforeEffect)
        {
            IDictionary sdkChildren = (IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("originalChildren", hidden)!.GetValue(sdk)!;
            object sdkChild = sdkChildren.Values.Cast<object>().Single(x => x.GetType().GetProperty("NestedParent")!.GetValue(x) is not null);
            object? state = sdkChild.GetType().GetProperty("State")!.GetValue(sdkChild);
            object? revision = sdkChild.GetType().GetProperty("StateRevision")!.GetValue(sdkChild);
            spy.LoseRootReply = false; spy.AfterRootClose(); spy.AfterRootClose = null;
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(0, nativeSpy.Calls.GetValueOrDefault("TransitionChildDomain"));
            Assert.Equal(state, sdkChild.GetType().GetProperty("State")!.GetValue(sdkChild));
            Assert.Equal(revision, sdkChild.GetType().GetProperty("StateRevision")!.GetValue(sdkChild));
            Assert.Equal(0, nativeSpy.Calls.GetValueOrDefault("CloseOriginalChild"));
            Assert.Equal(0, spy.RootRevokes);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Restore();
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        }
        else
        {
        for (int attempt = 0; attempt < 8 && spy.RootRevokes == 0; attempt++)
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, spy.RootRevokes);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Restore();
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        }
        Assert.All(kernel.QueryBudget(budget).Value!.Usage, x => Assert.Equal(0UL, x.Used));
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.All(foreign.Span.ToArray(), x => Assert.Equal((byte)0x73, x));
        Assert.Equal(1, nativeSpy.Calls.GetValueOrDefault("CreateOriginalNestedChild"));
        Assert.Equal(2, nativeSpy.Calls.GetValueOrDefault("CloseOriginalChild"));
        Assert.Equal(1, spy.RootRevokes);
    }
    public interface IGuestReplyProvider : ExactArtifactReleaseConsumerTests.IReleaseBridgeProvider, IPlatformDomainRevocationNoEffectProvider { }
    [Theory]
    [InlineData("ClosureReceipt")]
    [InlineData("OriginalUnmapInvocation")]
    [InlineData("ClosureSource")]
    [InlineData("Observation")]
    [InlineData("InvocationCopy")]
    [InlineData("FinalInvocationLoss")]
    [InlineData("ChildInvocationLoss")]
    [InlineData("ChildObservationLoss")]
    [InlineData("ChildInvocationCopy")]
    [InlineData("ChildFinalInvocationLoss")]
    [InlineData("ChildRouteLoss")]
    [InlineData("ChildRouteCopy")]
    [InlineData("ChildFactoryLoss")]
    [InlineData("ChildFactoryDuplicate")]
    [InlineData("ChildFactoryChildCopy")]
    [InlineData("ChildFactoryPreEffectLoss")]
    [InlineData("ChildFactoryPendingAdmission")]
    public void PublishedRootRetainsBackingUntilOriginalGuestClosureIsPresent(string property)
    {
        HybridCpuExternalRuntime native = new();
        var nativeProxy = DispatchProxy.Create<ExactArtifactReleaseConsumerTests.IReleaseRuntime, ExactArtifactReleaseConsumerTests.ReleaseSpy>();
        var nativeSpy = (ExactArtifactReleaseConsumerTests.ReleaseSpy)(object)nativeProxy; nativeSpy.Inner = native;
        ConstructorInfo constructor = typeof(HybridCpuExecutableChildAdapter).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(IHybridCpuExternalRuntime), typeof(IHybridCpuChildDomainRuntimeV3), typeof(ISemanticTraceSinkV1)], null)!;
        var adapter = (HybridCpuExecutableChildAdapter)constructor.Invoke([native, nativeProxy, null]);
        HybridCpuPlatformAuthorityProvider provider = new(new NeutralDomainRuntimeFacade(), adapter);
        var proxy = DispatchProxy.Create<IGuestReplyProvider, OriginalLoanRefusalConsumerTests.DeviceReplySpy>();
        var spy = (OriginalLoanRefusalConsumerTests.DeviceReplySpy)(object)proxy;
        spy.Inner = provider; spy.LoseReply = false; spy.LoseRootReply = true;
        RuntimeKernel kernel = new(proxy);
        ProcessHandle owner = TestFixtures.Create(kernel, 3871, 38710).Handle;
        ProcessHandle admin = TestFixtures.Create(kernel, 3872, 38720).Handle;
        CapabilityId budgetCap = kernel.MintCapability(new(38720), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "guest-custody",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 8192), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 8192)]).Value!.ProcessBudget;
        OwnedBuffer<byte> region = kernel.AllocateBuffer<byte>(owner, 8192).Value!;
        OwnedBuffer<byte> foreign = kernel.AllocateBuffer<byte>(admin, 16).Value!; foreign.Span.Fill(0x73);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId cap = kernel.MintCapability(binding.Subject.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        PlatformOwnedRegionSliceMapping mapping = kernel.MapPlatformOwnedRegionSlice(owner, binding, cap, region.Handle, 0, 8192, PlatformMemoryAccess.Read).Value!;
        const PlatformChildAuthorityClass authority = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory;
        PlatformChildBinding child = kernel.PlatformAuthority.CreateChildDomain(binding, binding.Subject, new(new(1, 8192), new(authority, authority))).Value!;
        PlatformGuestMapping mapped = kernel.PlatformAuthority.MapChildGuestRegion(child, mapping.Mapping, new(0, 8192), PlatformGuestMemoryAccess.Read).Value!;
        IDictionary originals = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderGuestMappings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(provider)!;
        object guest = Assert.Single(originals.Values.Cast<object>());
        if (property == "FinalInvocationLoss")
        {
            PropertyInfo invocationRoute = guest.GetType().GetProperty("OriginalUnmapInvocation")!;
            object? original = null;
            nativeSpy.AfterGuestUnmap = () => { original = invocationRoute.GetValue(guest); Assert.NotNull(original); invocationRoute.SetValue(guest, null); };
            Assert.False(kernel.PlatformAuthority.UnmapChildGuestRegion(mapped).IsSuccess);
            Assert.Equal(1, nativeSpy.UnmapCalls);
            invocationRoute.SetValue(guest, original); nativeSpy.AfterGuestUnmap = null;
            property = "OriginalUnmapInvocation";
        }
        Assert.True(kernel.PlatformAuthority.UnmapChildGuestRegion(mapped).IsSuccess);
        Assert.True(kernel.PlatformAuthority.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        object childOwner = Assert.Single(((IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderChildren", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(provider)!).Values.Cast<object>());
        if (property == "ChildFinalInvocationLoss")
        {
            PropertyInfo invocationRoute = childOwner.GetType().GetProperty("OriginalCloseInvocation")!;
            object? original = null;
            nativeSpy.AfterChildClose = () => { original = invocationRoute.GetValue(childOwner); Assert.NotNull(original); invocationRoute.SetValue(childOwner, null); };
            Assert.False(kernel.PlatformAuthority.CloseChildDomain(child).IsSuccess);
            Assert.Equal(1, nativeSpy.ChildCloseCalls);
            invocationRoute.SetValue(childOwner, original); nativeSpy.AfterChildClose = null;
            property = "ChildInvocationLoss";
        }
        Assert.True(kernel.PlatformAuthority.CloseChildDomain(child).IsSuccess);
        Action restore = () => { };
        if (property is "ChildRouteLoss" or "ChildRouteCopy")
        {
            IDictionary childRoutes = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderChildren", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(provider)!;
            object key = Assert.Single(childRoutes.Keys.Cast<object>());
            spy.AfterRootClose = () =>
            {
                if (property == "ChildRouteLoss") childRoutes.Remove(key);
                else childRoutes[key] = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(childOwner, null)!;
            };
            restore = () => childRoutes[key] = childOwner;
        }
        else if (property.StartsWith("ChildFactory", StringComparison.Ordinal))
        {
            object parent = childOwner.GetType().GetProperty("CreateParent")!.GetValue(childOwner)!;
            IList history = (IList)parent.GetType().GetProperty("OriginalChildCreates")!.GetValue(parent)!;
            object factory = Assert.Single(history.Cast<object>());
            spy.AfterRootClose = () =>
            {
                if (property is "ChildFactoryLoss" or "ChildFactoryPreEffectLoss") history.Clear();
                else if (property == "ChildFactoryDuplicate") history.Add(factory);
                else if (property == "ChildFactoryPendingAdmission") factory.GetType().GetProperty("Child")!.SetValue(factory, null);
                else factory.GetType().GetProperty("Child")!.SetValue(factory,
                    typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(childOwner, null));
            };
            restore = () => { history.Clear(); history.Add(factory); factory.GetType().GetProperty("Child")!.SetValue(factory, childOwner); };
        }
        else
        {
        object closureOwner = property.StartsWith("Child", StringComparison.Ordinal) ? childOwner : guest;
        string invocationName = ReferenceEquals(closureOwner, childOwner) ? "OriginalCloseInvocation" : "OriginalUnmapInvocation";
        object target = property is "Observation" or "ChildObservationLoss" ? closureOwner.GetType().GetProperty(invocationName)!.GetValue(closureOwner)! : closureOwner;
        string routeName = property switch
        {
            "InvocationCopy" or "ChildInvocationCopy" or "ChildInvocationLoss" => invocationName,
            "ChildObservationLoss" => "Observation",
            _ => property
        };
        PropertyInfo route = target.GetType().GetProperty(routeName)!;
        object? retained = null;
        spy.AfterRootClose = () =>
        {
            retained = route.GetValue(target); Assert.NotNull(retained);
            route.SetValue(target, property is "InvocationCopy" or "ChildInvocationCopy"
                ? typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(retained, null) : null);
        };
        restore = () => route.SetValue(target, retained);
        }
        int expectedRevokes = 1;
        if (property == "ChildFactoryPendingAdmission")
        {
            spy.AfterRootClose!();
            Assert.False(kernel.PlatformAuthority.CreateChildDomain(binding, binding.Subject, new(new(1, 8192), new(authority, authority))).IsSuccess);
            Assert.Equal(1, nativeSpy.CreateCalls);
            restore();
        }
        if (property == "ChildFactoryPreEffectLoss")
        {
            Action mutation = spy.AfterRootClose!;
            spy.AfterRootClose = null; mutation();
            for (int attempt = 0; attempt < 8 && spy.RootRevokes == 0; attempt++)
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1, spy.RootRevokes);
            Assert.Equal(0, nativeSpy.ParentCloseCalls);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            restore(); spy.AfterRootClose = mutation; expectedRevokes = 2;
        }
        for (int attempt = 0; attempt < 8 && spy.RootRevokes < expectedRevokes; attempt++)
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(expectedRevokes, spy.RootRevokes);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        restore(); spy.AfterRootClose = null;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage, x => Assert.Equal(0UL, x.Used));
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.All(foreign.Span.ToArray(), x => Assert.Equal((byte)0x73, x));
        Assert.Equal(expectedRevokes, spy.RootRevokes);
        Assert.Equal(1, nativeSpy.UnmapCalls);
        Assert.Equal(1, nativeSpy.CreateCalls);
        Assert.Equal(1, nativeSpy.ChildCloseCalls);
        Assert.Equal(1, nativeSpy.ParentCloseCalls);
    }
}
