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

public sealed partial class ExactArtifactReleaseConsumerTests
{
    public interface IOriginalStartReleaseRuntime : IReleaseRuntime, IHybridCpuOriginalChildExecutionStartRuntimeV1 { }
    [Theory]
    [InlineData("known")] [InlineData("lost-reply")] [InlineData("reentry")] [InlineData("reentry-teardown")]
    [InlineData("provider-history")] [InlineData("adapter-history")] [InlineData("not-invoked")]
    [InlineData("provider-aba")] [InlineData("adapter-aba")] [InlineData("source-aba")]
    [InlineData("native-no-effect")] [InlineData("native-no-effect-lost-reply")]
    [InlineData("native-no-effect-query-loss")] [InlineData("native-no-effect-foreign-query")] [InlineData("native-no-effect-source-aba")]
    public void OriginalStartRetainedBeforeActualExternalCallControlsArtifactRegionAndCharge(string fault)
    {
        bool noEffectReplyLost = fault.StartsWith("native-no-effect-");
        HybridCpuExternalRuntime native = new HybridCpuExternalRuntime();
        IOriginalStartReleaseRuntime proxy = DispatchProxy.Create<IOriginalStartReleaseRuntime,ReleaseSpy>(); ReleaseSpy spy = (ReleaseSpy)proxy; spy.Inner = native;
        IHybridCpuExternalRuntime root = DispatchProxy.Create<IHybridCpuExternalRuntime,RootSpy>(); RootSpy rootSpy = (RootSpy)root; rootSpy.Inner = native;
        ConstructorInfo constructor = typeof(HybridCpuExecutableChildAdapter).GetConstructor(Private,null,
            [typeof(IHybridCpuExternalRuntime),typeof(IHybridCpuChildDomainRuntimeV3),typeof(ISemanticTraceSinkV1)],null)!;
        HybridCpuExecutableChildAdapter adapter = (HybridCpuExecutableChildAdapter)constructor.Invoke([root,proxy,null]);
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(),adapter);
        RuntimeKernel kernel = new RuntimeKernel(provider); (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel,776,1118);
        ProcessHandle admin = TestFixtures.Create(kernel,98,998).Handle;
        CapabilityId budgetCap = kernel.MintCapability(new DomainId(998),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(admin,budgetCap,owner,"original-start",
            [new(ServiceBudgetDimension.OwnedMemoryBytes,8192),new(ServiceBudgetDimension.PinnedMappedMemoryBytes,8192)]).Value!.ProcessBudget;
        OwnedBuffer<byte> region = kernel.AllocateBuffer<byte>(owner,8192).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId cap = kernel.MintCapability(binding.Subject.DomainId,owner,ResourceKind.MemoryRegion,CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        PlatformOwnedRegionSliceMapping mapping = kernel.MapPlatformOwnedRegionSlice(owner,binding,cap,region.Handle,0,8192,PlatformMemoryAccess.Read).Value!;
        PlatformAuthorityBridge bridge = kernel.PlatformAuthority;
        const PlatformChildAuthorityClass rights = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.Execution | PlatformChildAuthorityClass.GuestMemory;
        PlatformChildBinding child = bridge.CreateChildDomain(binding,binding.Subject,new(new(1,8192),new(rights,rights))).Value!;
        PlatformGuestMapping guest = bridge.MapChildGuestRegion(child,mapping.Mapping,new(0,8192),PlatformGuestMemoryAccess.Read | PlatformGuestMemoryAccess.Execute).Value!;
        PlatformExecutableArtifactBinding artifact = bridge.BindChildExecutableArtifact(child,guest,ExecutableAdapterStartAdmissionTests.CreateExecutablePackage(),3).Value!;
        object providerOwner = Assert.Single(((IDictionary)provider.GetType().GetField("_originalProviderArtifacts",Private)!.GetValue(provider)!).Values.Cast<object>());
        object adapterOwner = Assert.Single(((IDictionary)adapter.GetType().GetField("originalArtifacts",Private)!.GetValue(adapter)!).Values.Cast<object>());
        object? providerIntent = null,adapterIntent = null; bool observed = false; bool rawObserved = false;
        object? olderProvider = null,olderAdapter = null;
        if(fault is "provider-aba" or "adapter-aba")
        {
            rootSpy.ThrowSource = true;
            Assert.False(bridge.StartChildExecutableArtifact(artifact).IsSuccess); Assert.Equal(0,spy.StartCalls);
            olderProvider = providerOwner.GetType().GetProperty("OriginalExecution")!.GetValue(providerOwner);
            olderAdapter = adapterOwner.GetType().GetProperty("OriginalStart")!.GetValue(adapterOwner);
            Assert.NotNull(olderProvider); Assert.NotNull(olderAdapter);
            rootSpy.ThrowSource = false;
        }
        spy.Fault = (fault == "lost-reply" || noEffectReplyLost) ? "failed-start-lost-reply" : string.Empty;
        spy.StartQueryFault = fault == "native-no-effect-query-loss" ? "loss" : fault == "native-no-effect-foreign-query" ? "foreign" : null;
        spy.BeforeStart = () =>
        {
            foreach((object? target, string? field) in new[] { ((object)provider,"_childCreateCommitGate"),((object)adapter,"sync"),((object)kernel,"_platformMemoryUseGate") })
                Assert.False(Monitor.IsEntered(target.GetType().GetField(field,Private)!.GetValue(target)!));
            providerIntent = providerOwner.GetType().GetProperty("OriginalExecution")!.GetValue(providerOwner);
            adapterIntent = adapterOwner.GetType().GetProperty("OriginalStart")!.GetValue(adapterOwner);
            Assert.NotNull(providerIntent); Assert.NotNull(adapterIntent);
            Assert.Null(providerIntent.GetType().GetProperty("Observation")!.GetValue(providerIntent));
            Assert.Null(adapterIntent.GetType().GetProperty("Observation")!.GetValue(adapterIntent));
            if (fault.StartsWith("native-no-effect")) typeof(HybridCpuExternalRuntime).GetField("nextExecutionGeneration", Private)!.SetValue(native, long.MaxValue);
            if(fault != "lost-reply") rootSpy.BeforeQuery = () =>
            {
                Assert.False(Monitor.IsEntered(adapter.GetType().GetField("sync",Private)!.GetValue(adapter)!));
                if (noEffectReplyLost)
                {
                    Assert.Equal(fault == "native-no-effect-query-loss", adapterIntent.GetType().GetProperty("ResolutionObservation")!.GetValue(adapterIntent) is null);
                    if (fault == "native-no-effect-source-aba") rootSpy.FeatureGenerationOverride = native.QueryFeatures().Generation + 1;
                }
                else Assert.NotNull(adapterIntent.GetType().GetProperty("Observation")!.GetValue(adapterIntent));
                rawObserved = true;
            };
            observed = true;
            if(fault is "reentry" or "reentry-teardown")
            {
                Assert.False(bridge.StartChildExecutableArtifact(artifact).IsSuccess);
                Assert.False(bridge.UnmapChildGuestRegion(guest).IsSuccess);
                if(fault == "reentry-teardown") Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            }
            if(fault == "provider-history") ((IList)providerOwner.GetType().GetProperty("ExecutionHistory")!.GetValue(providerOwner)!).Clear();
            if(fault == "adapter-history") ((IList)adapterOwner.GetType().GetProperty("StartHistory")!.GetValue(adapterOwner)!).Clear();
            if(fault == "provider-aba") providerOwner.GetType().GetProperty("OriginalExecution")!.SetValue(providerOwner,olderProvider);
            if(fault == "adapter-aba") adapterOwner.GetType().GetProperty("OriginalStart")!.SetValue(adapterOwner,olderAdapter);
            if(fault == "source-aba") rootSpy.FeatureGenerationOverride = native.QueryFeatures().Generation + 1;
        };
        FieldInfo operationField = adapter.GetType().GetField("nextOperation",Private)!;
        object? savedOperation = operationField.GetValue(adapter);
        if(fault == "not-invoked") operationField.SetValue(adapter,0UL);
        _ = bridge.StartChildExecutableArtifact(artifact);
        Assert.Equal(fault != "not-invoked",observed); Assert.Equal(fault == "not-invoked" ? 0 : 1,spy.StartCalls);
        Assert.Equal(fault is not ("not-invoked" or "lost-reply"),rawObserved);
        Assert.Equal(spy.StartCalls, spy.OriginalStartCalls);
        Assert.Equal((fault == "lost-reply" || noEffectReplyLost) ? 1 : 0, spy.OriginalStartQueries);
        if (fault == "lost-reply" || fault == "native-no-effect-lost-reply")
        {
            ExternalOriginalChildExecutionStartObservation resolved = Assert.IsType<ExternalOriginalChildExecutionStartObservation>(adapterIntent!.GetType().GetProperty("ResolutionObservation")!.GetValue(adapterIntent));
            Assert.Same(adapterIntent.GetType().GetProperty("OriginalExternalRequest")!.GetValue(adapterIntent), resolved.Request);
            Assert.Equal(fault == "native-no-effect-lost-reply", resolved.NoEffect);
            Assert.Same(native.ResolveOriginalChildExecutionStart(resolved.Request)!.Raw, resolved.Raw);
            Assert.Equal(fault == "native-no-effect-lost-reply" ? ExternalRuntimeOutcome.Denied : ExternalRuntimeOutcome.Faulted, resolved.Raw.Outcome);
        }
        rootSpy.BeforeQuery = null;
        if(fault != "not-invoked")
        {
            if(fault != "provider-aba") Assert.Same(providerIntent,providerOwner.GetType().GetProperty("OriginalExecution")!.GetValue(providerOwner));
            if(fault != "adapter-aba") Assert.Same(adapterIntent,adapterOwner.GetType().GetProperty("OriginalStart")!.GetValue(adapterOwner));
            Assert.NotNull(providerIntent!.GetType().GetProperty("Observation")!.GetValue(providerIntent));
            Assert.Equal((fault == "lost-reply" || noEffectReplyLost),adapterIntent!.GetType().GetProperty("Observation")!.GetValue(adapterIntent) is null);
            Assert.False(bridge.StartChildExecutableArtifact(artifact).IsSuccess);
        }
        else
        {
            object noEffect = providerOwner.GetType().GetProperty("OriginalExecution")!.GetValue(providerOwner)!;
            Assert.True((bool)noEffect.GetType().GetProperty("NoEffectConsumed")!.GetValue(noEffect)!);
            operationField.SetValue(adapter,savedOperation);
        }
        if (fault.StartsWith("native-no-effect"))
        {
            Assert.Equal(fault is "native-no-effect" or "native-no-effect-lost-reply", (bool)adapterIntent!.GetType().GetProperty("NoEffectConsumed")!.GetValue(adapterIntent)!);
            IDictionary nativeChildren = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(native)!;
            object nativeChild = Assert.Single(nativeChildren.Values.Cast<object>());
            Assert.Equal(0UL, nativeChild.GetType().GetProperty("ExecutionStartsIssued")!.GetValue(nativeChild));
            Assert.NotNull(nativeChild.GetType().GetProperty("Execution")!.GetValue(nativeChild)); // No-effect alone has not released backing.
            object[] refusals = ((IEnumerable)nativeChild.GetType().GetProperty("OriginalStartRefusals")!.GetValue(nativeChild)!).Cast<object>().ToArray();
            Assert.Equal(fault == "native-no-effect" ? 2 : 1, refusals.Length);
            if (refusals.Length == 2) Assert.NotSame(refusals[0].GetType().GetProperty("Original")!.GetValue(refusals[0]), refusals[1].GetType().GetProperty("Original")!.GetValue(refusals[1]));
        }
        spy.BeforeStart = null;
        rootSpy.FeatureGenerationOverride = null; // Equal/ABA source restoration does not discharge the observed source fault.
        Assert.False((bool)providerOwner.GetType().GetProperty("ExecutionInFlight")!.GetValue(providerOwner)!);
        Assert.Empty((IEnumerable)((HashSet<NeutralChildDomainHandle>)adapter.GetType().GetField("pendingChildStarts",Private)!.GetValue(adapter)!));
        KernelResult closed = bridge.UnmapChildGuestRegion(guest);
        if(fault is "provider-history" or "adapter-history" or "provider-aba" or "adapter-aba" or "source-aba" or "native-no-effect-source-aba")
        {
            Assert.False(closed.IsSuccess); Assert.Equal(0,spy.ReleaseCalls); Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
            Assert.Equal(8192UL,kernel.QueryBudget(budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        else
        {
            Assert.True(closed.IsSuccess,closed.Message);
            Assert.True(bridge.TransitionChildDomain(child,PlatformChildDomainTransition.BeginDrain).IsSuccess);
            Assert.True(bridge.CloseChildDomain(child).IsSuccess); Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
            Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        }
        Assert.Equal(fault == "not-invoked" ? 0 : fault == "native-no-effect" ? 2 : 1,spy.StartCalls);
    }
}
