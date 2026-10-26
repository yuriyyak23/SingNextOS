using System.Collections;
using System.Reflection;
using HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Tests.Platform;
public sealed class ExactNestedArtifactConsumerTests
{
    public interface IOriginalNestedStartRuntime : ExactNestedDomainConsumerTests.INestedRuntime, IHybridCpuOriginalChildExecutionStartRuntimeV1, IHybridCpuPrivateExecutableDataQuotaRuntimeV1, IHybridCpuOriginalExecutionTerminalRuntimeV1 { }
    public interface IOriginalNestedStartWithoutQuotaRuntime : ExactNestedDomainConsumerTests.INestedRuntime, IHybridCpuOriginalChildExecutionStartRuntimeV1 { }
    public class ArtifactSpy : ExactNestedGuestConsumerTests.GuestSpy
    {
        public string BindFault = "exact";
        public Action<string>? Before;
        public string StartFault = "exact";
        public int StartQueryFailures;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "BindChildExecutableArtifact") Assert.Equal(16384UL, ((ExternalChildArtifactBindRequest)args![1]!).MaximumPrivateDataBytes);
            if (method!.Name == "ResolveOriginalChildExecutionStart" && StartQueryFailures > 0)
            {
                StartQueryFailures--;
                throw new IOException("Original Start pure query reply lost.");
            }
            Before?.Invoke(method!.Name);
            object? raw = base.Invoke(method, args);
            if (method!.Name == "StartOriginalChildExecution" && StartFault == "lost-reply") throw new IOException("Original Start reply lost after effect.");
            if (method.Name == "StartOriginalChildExecution" && StartFault is "copy" or "copy-query-loss" && raw is ExternalOriginalChildExecutionStartObservation start)
                return start with { Raw = start.Raw with { Receipt = start.Raw.Receipt is { } receiptCopy ? receiptCopy with { } : null } };
            if (method.Name == "StartOriginalChildExecution" && StartFault == "forged-no-effect" && raw is ExternalOriginalChildExecutionStartObservation invoked)
                return invoked with { NoEffect = true, Raw = new(ExternalRuntimeOutcome.Denied, null, "Forged NoEffect after actual execution.") };
            if (method.Name == "BindChildExecutableArtifact" && BindFault == "lost-reply") throw new IOException("Original Bind reply lost after backing effect.");
            if (method.Name == "ReleaseChildExecutableArtifact" && BindFault == "release-lost-reply") throw new IOException("Original Release reply lost after closure effect.");
            if (method.Name == "BindChildExecutableArtifact" && BindFault == "copy" && raw is ExternalChildResult<ExternalChildArtifactBindReceipt> result && result.Receipt is { } receipt)
                return result with { Receipt = receipt with { } };
            if (method.Name == "BindChildExecutableArtifact" && BindFault == "quota-forged-receipt" && raw is ExternalChildResult<ExternalChildArtifactBindReceipt> quotaResult && quotaResult.Receipt is { } quotaReceipt)
                return quotaResult with { Receipt = quotaReceipt with { MaximumPrivateDataBytes = 0 } };
            return raw;
        }
    }
    [Theory]
    [InlineData("exact")] [InlineData("native-before")] [InlineData("native-after")]
    [InlineData("sdk-history")] [InlineData("provider-history")] [InlineData("bridge-aba")]
    [InlineData("lost-reply")] [InlineData("copy")]
    [InlineData("query-reentry")] [InlineData("independent-revoke")]
    [InlineData("release-lost-reply")] [InlineData("partial-consumption")]
    [InlineData("release-request-loss")]
    [InlineData("release-pre-source")]
    [InlineData("release-reentry")]
    [InlineData("unmap-partial-consumption")]
    [InlineData("source-before")] [InlineData("source-aba")] [InlineData("source-loss")]
    [InlineData("start-exact")] [InlineData("start-reentry")] [InlineData("start-native-before")] [InlineData("start-native-after")]
    [InlineData("start-bridge-aba")] [InlineData("start-lost-reply")] [InlineData("start-copy")]
    [InlineData("start-no-effect")] [InlineData("start-forged-no-effect")] [InlineData("start-copy-query-loss")]
    [InlineData("start-query-unknown")] [InlineData("start-sdk-aba")] [InlineData("start-sdk-exhaustion")]
    [InlineData("start-source-aba")] [InlineData("start-source-loss")] [InlineData("start-provider-history")]
    [InlineData("sdk-child-aba")] [InlineData("sdk-revision-exhaustion")] [InlineData("native-parent-copy")]
    [InlineData("quota-capacity-denied")] [InlineData("quota-missing")] [InlineData("quota-invalid")]
    [InlineData("quota-copied-charge")] [InlineData("quota-forged-receipt")]
    [InlineData("quota-incapable")]
    [InlineData("start-terminal-query")]
    public void ActualNestedArtifactBindRequiresOriginalLineageAndKeepsBackingUntilExactClosure(string mode)
    {
        IHybridCpuChildDomainRuntimeV3 native = mode == "quota-incapable"
            ? DispatchProxy.Create<IOriginalNestedStartWithoutQuotaRuntime, ArtifactSpy>()
            : DispatchProxy.Create<IOriginalNestedStartRuntime, ArtifactSpy>();
        ArtifactSpy spy = (ArtifactSpy)(object)native; spy.BindFault = mode == "query-reentry" ? "lost-reply" : mode;
        ExactArtifactReleaseConsumerTests.IOriginalRootRuntime rootRuntime = DispatchProxy.Create<ExactArtifactReleaseConsumerTests.IOriginalRootRuntime, ExactArtifactReleaseConsumerTests.RootSpy>();
        ExactArtifactReleaseConsumerTests.RootSpy rootSpy = (ExactArtifactReleaseConsumerTests.RootSpy)(object)rootRuntime; rootSpy.Inner = spy.Inner;
        HybridCpuExecutableChildAdapter sdk = new HybridCpuExecutableChildAdapter(rootRuntime, native);
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), sdk);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 788, 1132);
        ProcessHandle admin = TestFixtures.Create(kernel, 99, 999).Handle;
        CapabilityId budgetCap = kernel.MintCapability(new(999), admin, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        // Current compiler-produced Start image is12316 bytes (page-aligned code/metadata), with payload8220.
        // Admit its bounded storage explicitly; private quota and all non-Start refusal cases stay unchanged.
        int codeBytes = mode.StartsWith("start-", StringComparison.Ordinal) ? 16384 : 8192;
        ulong codeCharge = (ulong)codeBytes;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "nested-artifact", [new(ServiceBudgetDimension.OwnedMemoryBytes, codeCharge + 16384UL - (mode == "quota-capacity-denied" ? 1UL : 0UL)), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, codeCharge), new(ServiceBudgetDimension.GuestMemoryBytes, codeCharge), new(ServiceBudgetDimension.RegionUses, 8)]).Value!.ProcessBudget;
        OwnedBuffer<byte> region = kernel.AllocateBuffer<byte>(owner, codeBytes).Value!;
        CapabilityId create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet root = kernel.CreateVirtualDomain(owner, create, new(1, codeBytes * 2L)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, root.Domain, root.ConfigureCapability).IsSuccess);
        VirtualDomainAuthoritySet child = kernel.CreateNestedVirtualDomain(owner, root.Domain, root.ConfigureCapability,
            new(new(1, codeBytes), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.GuestMemory | VirtualDomainAuthorityClass.Execution |
                (mode == "start-terminal-query" ? VirtualDomainAuthorityClass.Traps : VirtualDomainAuthorityClass.None))).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, child.Domain, child.ConfigureCapability).IsSuccess);
        CapabilityId memory = kernel.MintCapability(process.DomainId, owner, ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Execute).Value!.CapabilityId;
        KernelResult<GuestRegionMapping> mapped = kernel.MapGuestRegion(owner, child.Domain, child.MemoryCapability, memory, region.Handle, new(0, codeBytes), GuestMemoryAccess.Read | GuestMemoryAccess.Execute,
            mode == "quota-missing" ? 0UL : mode == "quota-invalid" ? 16385UL : 16384UL);
        if (mode is "quota-capacity-denied" or "quota-invalid")
        {
            Assert.False(mapped.IsSuccess);
            Assert.Equal(0, spy.Calls.GetValueOrDefault("MapChildGuestMemory"));
            Assert.Equal(codeCharge, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.All(kernel.QueryBudget(budget).Value!.Usage, usage => Assert.Equal(0UL, usage.Used));
            return;
        }
        Assert.True(mapped.IsSuccess, mapped.Message);
        if (mode is "quota-missing" or "quota-copied-charge")
        {
            IDictionary? reservations = null; object? key = null, original = null;
            if (mode == "quota-copied-charge")
            {
                reservations = (IDictionary)Field(kernel.Budgets, "_reservations");
                key = reservations.Keys.Cast<object>().Single(x => Property(reservations[x]!, "Lifetime")!.Equals(BudgetReservationLifetime.PlatformMapping));
                original = reservations[key];
                reservations[key] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(original, null);
            }
            Assert.False(kernel.BindVirtualDomainExecutable(owner, child.Domain, child.ExecuteCapability, mapped.Value!.Mapping, ReadActualStartPackage(), 512).IsSuccess);
            Assert.Equal(0, spy.Calls.GetValueOrDefault("BindChildExecutableArtifact"));
            if (reservations is not null)
            {
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                Assert.Equal(codeCharge + 16384UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
                reservations[key!] = original;
            }
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.All(kernel.QueryBudget(budget).Value!.Usage, usage => Assert.Equal(0UL, usage.Used));
            return;
        }
        IList parentHistory = (IList)spy.NativeParent.GetType().GetProperty("OriginalNestedChildren")!.GetValue(spy.NativeParent)!;
        spy.Before = name =>
        {
            if (name == "BindChildExecutableArtifact")
                Assert.Equal(codeCharge + 16384UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        };
        if (mode == "native-before") parentHistory.Clear();
        if (mode == "source-before") rootSpy.FeatureGenerationOverride = spy.Inner.QueryFeatures().Generation + 1;
        spy.After = name =>
        {
            if (name != "BindChildExecutableArtifact") return;
            Assert.False(Monitor.IsEntered(Field(sdk, "sync")));
            Assert.False(Monitor.IsEntered(Field(provider, "_childCreateCommitGate")));
            Assert.False(Monitor.IsEntered(Field(kernel.PlatformAuthority, "_secureDomainLifecycleGate")));
            if (mode == "native-after") parentHistory.Clear();
            if (mode is "source-aba" or "source-loss")
            {
                rootSpy.BeforeQuery = () =>
                {
                    Assert.False(Monitor.IsEntered(Field(sdk, "sync")));
                    object mapping = ((IDictionary)Field(sdk, "originalMappings")).Values.Cast<object>().Single();
                    object fact = Property(mapping, "ArtifactBind")!;
                    Assert.NotNull(Property(fact, "Observation"));
                };
                if (mode == "source-aba") rootSpy.FeatureGenerationOverride = spy.Inner.QueryFeatures().Generation + 1;
                else rootSpy.ThrowSource = true;
            }
            if (mode == "sdk-history") Clear(((IDictionary)Field(sdk, "originalChildren")).Values.Cast<object>().Single(x => Property(x, "NestedParent") is null), "OriginalNested");
            if (mode is "sdk-child-aba" or "sdk-revision-exhaustion")
            {
                object originalChild = ((IDictionary)Field(sdk, "originalChildren")).Values.Cast<object>().Single(x => Property(x, "NestedParent") is not null);
                if (mode == "sdk-revision-exhaustion") originalChild.GetType().GetField("_stateRevision", Private)!.SetValue(originalChild, long.MaxValue);
                originalChild.GetType().GetProperty("State")!.SetValue(originalChild, NeutralChildDomainState.Parked);
                originalChild.GetType().GetProperty("State")!.SetValue(originalChild, NeutralChildDomainState.Created);
            }
            if (mode == "native-parent-copy")
            {
                object admission = Property(spy.NativeParent, "OriginalAdmission")!;
                object? copy = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(admission, null);
                spy.NativeParent.GetType().GetProperty("OriginalAdmission")!.SetValue(spy.NativeParent, copy);
            }
            if (mode == "provider-history") Clear(((IDictionary)Field(provider, "_originalProviderChildren")).Values.Cast<object>().Single(x => Property(x, "NestedParent") is null), "OriginalNested");
            if (mode == "bridge-aba")
            {
                object parent = ((IDictionary)Field(kernel.PlatformAuthority, "_originalChildOwners")).Values.Cast<object>().Single(x => Property(x, "ParentChild") is null);
                parent.GetType().GetProperty("State")!.SetValue(parent, global::SingPlus.Platform.PlatformChildDomainState.Draining);
                parent.GetType().GetProperty("State")!.SetValue(parent, global::SingPlus.Platform.PlatformChildDomainState.Created);
            }
        };
        KernelResult bound = kernel.BindVirtualDomainExecutable(owner, child.Domain, child.ExecuteCapability, mapped.Value!.Mapping,
            mode.StartsWith("start-") ? ReadActualStartPackage() : ExecutableAdapterStartAdmissionTests.CreateExecutablePackage(), mode.StartsWith("start-") ? 512 : 3);
        bool expectedBind = mode.StartsWith("start-") || mode is "exact" or "independent-revoke" or "release-lost-reply" or "partial-consumption" or "release-request-loss" or "release-pre-source" or "release-reentry" or "unmap-partial-consumption";
        Assert.True(expectedBind == bound.IsSuccess, bound.Message);
        spy.After = null;
        if (mode == "quota-incapable")
        {
            Assert.False(bound.IsSuccess);
            Assert.Equal(0, spy.Calls.GetValueOrDefault("BindChildExecutableArtifact"));
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.All(kernel.QueryBudget(budget).Value!.Usage, usage => Assert.Equal(0UL, usage.Used));
            Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            return;
        }
        rootSpy.BeforeQuery = null;
        if (mode != "source-before") rootSpy.FeatureGenerationOverride = null;
        rootSpy.ThrowSource = false;
        IDictionary bridgeParents = (IDictionary)Field(kernel.PlatformAuthority, "_originalChildOwners");
        object rootOwner = bridgeParents.Values.Cast<object>().Single(x => Property(x, "ParentChild") is null);
        if (mode == "query-reentry") spy.After = name =>
        {
            if (name != "ResolveChildExecutableArtifactBind") return;
            Assert.False(Monitor.IsEntered(Field(kernel.PlatformAuthority, "_secureDomainLifecycleGate")));
            Assert.False(kernel.PlatformAuthority.TransitionChildDomain((PlatformChildBinding)Property(rootOwner, "Binding")!, global::SingPlus.Platform.PlatformChildDomainTransition.BeginDrain).IsSuccess);
        };
        if (mode.StartsWith("start-"))
        {
            Assert.True(bound.IsSuccess, bound.Message);
            spy.StartFault = mode == "start-lost-reply" ? "lost-reply" : mode == "start-copy" ? "copy" : "exact";
            if (mode == "start-native-before") parentHistory.Clear();
            if (mode == "start-copy-query-loss") { spy.StartFault = "copy-query-loss"; spy.StartQueryFailures = 1; }
            if (mode == "start-query-unknown") spy.StartQueryFailures = 2;
            if (mode == "start-forged-no-effect") spy.StartFault = "forged-no-effect";
            if (mode == "start-no-effect") Set(spy.NativeParent, "State", (ExternalChildDomainState)255);
            spy.After = name =>
            {
                if (name != "StartOriginalChildExecution") return;
                Assert.False(Monitor.IsEntered(Field(sdk, "sync")));
                Assert.False(Monitor.IsEntered(Field(provider, "_childCreateCommitGate")));
                Assert.False(Monitor.IsEntered(Field(kernel.PlatformAuthority, "_secureDomainLifecycleGate")));
                if (mode == "start-native-after") parentHistory.Clear();
                if (mode == "start-provider-history")
                    Clear(((IDictionary)Field(provider, "_originalProviderChildren")).Values.Cast<object>().Single(x => Property(x, "NestedParent") is null), "OriginalNested");
                if (mode is "start-sdk-aba" or "start-sdk-exhaustion")
                {
                    object ownLeaf = ((IDictionary)Field(sdk, "originalChildren")).Values.Cast<object>().Single(x => Property(x, "NestedParent") is not null);
                    if (mode == "start-sdk-exhaustion") ownLeaf.GetType().GetField("_stateRevision", Private)!.SetValue(ownLeaf, long.MaxValue);
                    Set(ownLeaf, "State", NeutralChildDomainState.Parked);
                    Set(ownLeaf, "State", NeutralChildDomainState.Created);
                }
                if (mode is "start-source-aba" or "start-source-loss") rootSpy.BeforeQuery = () =>
                {
                    object ownArtifact = ((IDictionary)Field(sdk, "originalArtifacts")).Values.Cast<object>().Single();
                    Assert.NotNull(Property(Property(ownArtifact, "OriginalStart")!, "Observation"));
                    Assert.False(Monitor.IsEntered(Field(sdk, "sync")));
                    if (mode == "start-source-aba") rootSpy.FeatureGenerationOverride = spy.Inner.QueryFeatures().Generation + 1;
                    else rootSpy.ThrowSource = true;
                };
                if (mode == "start-reentry")
                    Assert.False(kernel.PlatformAuthority.TransitionChildDomain((PlatformChildBinding)Property(rootOwner, "Binding")!, global::SingPlus.Platform.PlatformChildDomainTransition.BeginDrain).IsSuccess);
                if (mode == "start-bridge-aba")
                {
                    object ancestor = ((IDictionary)Field(kernel.PlatformAuthority, "_originalChildOwners")).Values.Cast<object>().Single(x => Property(x, "ParentChild") is null);
                    Set(ancestor, "State", global::SingPlus.Platform.PlatformChildDomainState.Draining);
                    Set(ancestor, "State", global::SingPlus.Platform.PlatformChildDomainState.Created);
                }
            };
            KernelResult started = kernel.StartVirtualDomain(owner, child.Domain, child.ExecuteCapability);
            Assert.True(started.IsSuccess == (mode is "start-exact" or "start-reentry" or "start-terminal-query"), started.Message);
            if (mode != "start-no-effect") Assert.False(kernel.StartVirtualDomain(owner, child.Domain, child.ExecuteCapability).IsSuccess);
            Assert.Equal(1, spy.Calls.GetValueOrDefault("StartOriginalChildExecution"));
            object leaf = ((IDictionary)Field(spy.Inner, "childRegistry")).Values.Cast<object>().Single(x => Property(x, "ImmediateParent") is not null);
            Assert.Equal(mode is "start-native-before" or "start-no-effect" ? 0UL : 1UL, (ulong)Property(leaf, "ExecutionStartsIssued")!);
            object privateData = Field(Property(leaf, "Execution")!, "memory");
            ulong allocatedData = (ulong)Property(privateData, "AllocatedDataBytes")!;
            Assert.True(allocatedData > 0);
            Assert.True(allocatedData <= 16384);
            Assert.Equal(16384UL, (ulong)Property(privateData, "MaximumDataBytes")!);
            spy.After = null;
            if (mode == "start-terminal-query")
            {
                object originalStart = Property(leaf, "OriginalExecutionStart")!;
                ExternalOriginalExecutionTerminalObservation nativeTerminal = Assert.IsType<ExternalOriginalExecutionTerminalObservation>(Property(originalStart, "TerminalObservation"));
                KernelResult<VirtualTrapObservation> terminal = kernel.ObserveVirtualTrap(owner, child.Domain, child.TrapCapability);
                Assert.Equal(nativeTerminal.Kind != ExternalExecutionTerminalKind.Completed, terminal.IsSuccess);
                Assert.Equal(nativeTerminal.Kind == ExternalExecutionTerminalKind.Completed ? VirtualDomainState.Completed : VirtualDomainState.Faulted,
                    kernel.QueryVirtualDomain(owner, child.Domain).Value);
                Assert.Equal(nativeTerminal.Kind == ExternalExecutionTerminalKind.Completed ? 1 : 2,
                    spy.Calls.GetValueOrDefault("ResolveOriginalExecutionTerminal"));
                Assert.Equal(1UL, (ulong)Property(leaf, "ExecutionStartsIssued")!);
            }
            KernelResult cleanup = kernel.TerminateProcess(owner);
            bool exact = mode is "start-exact" or "start-reentry" or "start-lost-reply" or "start-query-unknown" or "start-no-effect" or "start-terminal-query";
            if (exact)
            {
                Assert.True(cleanup.IsSuccess, cleanup.Message);
                Assert.Equal(1, spy.Calls.GetValueOrDefault("ReleaseChildExecutableArtifact"));
                Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
                Assert.All(kernel.QueryBudget(budget).Value!.Usage, usage => Assert.Equal(0UL, usage.Used));
                Assert.True((bool)Property(privateData, "Retired")!);
                Assert.Equal(0UL, (ulong)Property(privateData, "AllocatedDataBytes")!);
            }
            else
            {
                Assert.False(cleanup.IsSuccess);
                Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
                Assert.Equal(codeCharge, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.GuestMemoryBytes).Used);
                Assert.False((bool)Property(privateData, "Retired")!);
                Assert.Equal(allocatedData, (ulong)Property(privateData, "AllocatedDataBytes")!);
            }
            return;
        }
        if (mode == "independent-revoke")
        {
            object parentMapping = ((IDictionary)Field(kernel.PlatformAuthority, "_mappings")).Values.Cast<object>().Single();
            parentMapping.GetType().GetProperty("LocalAuthorizationRevoked")!.SetValue(parentMapping, true);
        }
        IDictionary? charges = null; object? chargeKey = null, originalCharge = null;
        if (mode is "partial-consumption" or "unmap-partial-consumption") spy.After = name =>
        {
            if (name != (mode=="partial-consumption" ? "ReleaseChildExecutableArtifact" : "UnmapOriginalGuest")) return;
            charges = (IDictionary)Field(kernel, "_mappingBudgetReservations");
            chargeKey = Assert.Single(charges.Keys.Cast<object>()); originalCharge = charges[chargeKey];
            charges.Remove(chargeKey);
        };
        object? bridgeArtifact=null, bridgeRelease=null;
        if(mode=="release-request-loss") spy.After=name=>
        {
            if(name!="ReleaseChildExecutableArtifact")return;
            bridgeArtifact=Assert.Single(((IDictionary)Field(kernel.PlatformAuthority,"_originalExecutableArtifactOwners")).Values.Cast<object>());
            bridgeRelease=Property(bridgeArtifact,"ReleaseRequest");
            bridgeArtifact.GetType().GetProperty("ReleaseRequest")!.SetValue(bridgeArtifact,null);
        };
        object? preparedRelease=null;
        int releaseReentries =0;
        if(mode=="release-reentry") spy.After=name=>
        {
            if(name!="ReleaseChildExecutableArtifact")return;
            releaseReentries++;
            Assert.False(Monitor.IsEntered(Field(sdk,"sync")));
            Assert.False(Monitor.IsEntered(Field(provider,"_childCreateCommitGate")));
            Assert.False(Monitor.IsEntered(Field(kernel.PlatformAuthority,"_secureDomainLifecycleGate")));
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x=>x.Handle==region.Handle).State);
            Assert.Equal(codeCharge,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        };
        if(mode=="release-pre-source") rootSpy.BeforeQuery=()=>
        {
            object? artifact =((IDictionary)Field(sdk,"originalArtifacts")).Values.Cast<object>().SingleOrDefault();
            if(artifact is not null && Property(artifact,"ReleaseRequest") is { } request)
            {
                preparedRelease ??= request;
                Assert.Equal(preparedRelease,request);
                throw new IOException("Release source callback interrupted before effect.");
            }
        };
        IDictionary nativeChildren = (IDictionary)Field(spy.Inner, "childRegistry");
        if (mode is "native-before" or "source-before") Assert.All(nativeChildren.Values.Cast<object>(), x => Assert.Null(Property(x, "Execution")));
        if (mode is "exact" or "lost-reply" or "query-reentry" or "release-lost-reply" or "partial-consumption" or "release-request-loss" or "release-pre-source" or "release-reentry" or "unmap-partial-consumption")
        {
            KernelResult terminated = kernel.TerminateProcess(owner);
            if (mode is "release-lost-reply" or "partial-consumption" or "release-request-loss" or "release-pre-source" or "unmap-partial-consumption")
            {
                Assert.False(terminated.IsSuccess);
                Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
                Assert.Equal(codeCharge, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
                spy.BindFault = "exact"; spy.After = null;
                if(mode=="release-pre-source")
                {
                    Assert.NotNull(preparedRelease);
                    Assert.Equal(0,spy.Calls.GetValueOrDefault("ReleaseChildExecutableArtifact"));
                    rootSpy.BeforeQuery=null;
                }
                if (mode is "partial-consumption" or "unmap-partial-consumption") charges![chargeKey!] = originalCharge;
                if(mode=="release-request-loss")
                {
                    Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                    Assert.Null(Property(bridgeArtifact!,"ReleaseRequest"));
                    Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseChildExecutableArtifact"));
                    bridgeArtifact!.GetType().GetProperty("ReleaseRequest")!.SetValue(bridgeArtifact,bridgeRelease);
                }
                terminated = kernel.TerminateProcess(owner);
            }
            Assert.True(terminated.IsSuccess, terminated.Message + " Calls=" + global::System.Text.Json.JsonSerializer.Serialize(spy.Calls));
            Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.All(kernel.QueryBudget(budget).Value!.Usage, x => Assert.Equal(0UL, x.Used));
            Assert.Equal(1, spy.Calls.GetValueOrDefault("BindChildExecutableArtifact"));
            Assert.Equal(1, spy.Calls.GetValueOrDefault("ReleaseChildExecutableArtifact"));
            if(mode=="unmap-partial-consumption") Assert.Equal(1,spy.Calls.GetValueOrDefault("UnmapOriginalGuest"));
            if(mode=="release-reentry")Assert.Equal(1,releaseReentries);
            if (mode == "exact")
            {
                object artifact = Assert.Single(((IDictionary)Field(sdk,"originalArtifacts")).Values.Cast<object>());
                NeutralArtifactReleaseRequest originalRelease = (NeutralArtifactReleaseRequest)Property(artifact,"ReleaseRequest")!;
                Assert.True(sdk.ResolveExecutableArtifactRelease(originalRelease).IsSuccess);
                Assert.False(sdk.ResolveExecutableArtifactRelease(originalRelease with { InvocationId=Guid.NewGuid() }).IsSuccess);
                artifact.GetType().GetProperty("ReleaseRequest")!.SetValue(artifact,null);
                Assert.False(sdk.ResolveExecutableArtifactRelease(originalRelease).IsSuccess);
                Assert.False(sdk.ReleaseExecutableArtifact(originalRelease with { InvocationId=Guid.NewGuid() }).IsSuccess);
                artifact.GetType().GetProperty("ReleaseRequest")!.SetValue(artifact,originalRelease);
                Assert.True(sdk.ResolveExecutableArtifactRelease(originalRelease).IsSuccess);
                Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseChildExecutableArtifact"));
                object completedMapping = Assert.Single(((IDictionary)Field(sdk,"originalMappings")).Values.Cast<object>());
                NeutralGuestMappingLease mappingLease =(NeutralGuestMappingLease)Property(completedMapping,"Neutral")!;
                object unmap =Property(completedMapping,"OriginalUnmap")!;
                FieldInfo validatedUnmap =unmap.GetType().GetField("Validated")!;
                object originalUnmap =validatedUnmap.GetValue(unmap)!;
                Assert.True(sdk.ResolveOriginalGuestUnmap(mappingLease).IsSuccess);
                validatedUnmap.SetValue(unmap,typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(originalUnmap,null));
                Assert.False(sdk.ResolveOriginalGuestUnmap(mappingLease).IsSuccess);
                validatedUnmap.SetValue(unmap,originalUnmap);
                Assert.True(sdk.ResolveOriginalGuestUnmap(mappingLease).IsSuccess);
                Assert.Equal(1,spy.Calls.GetValueOrDefault("UnmapOriginalGuest"));
                object platformArtifact = Assert.Single(((IDictionary)Field(provider,"_originalProviderArtifacts")).Values.Cast<object>());
                PlatformArtifactReleaseRequest platformRequest = (global::SingPlus.Platform.PlatformArtifactReleaseRequest)Property(platformArtifact,"ReleaseRequest")!;
                Assert.True(provider.ResolveExecutableArtifactRelease(platformRequest).IsSuccess);
                platformArtifact.GetType().GetProperty("ReleaseRequest")!.SetValue(platformArtifact,null);
                Assert.False(provider.ResolveExecutableArtifactRelease(platformRequest).IsSuccess);
                Assert.False(provider.ReleaseExecutableArtifact(platformRequest with { InvocationId=Guid.NewGuid() }).IsSuccess);
                platformArtifact.GetType().GetProperty("ReleaseRequest")!.SetValue(platformArtifact,platformRequest);
                Assert.True(provider.ResolveExecutableArtifactRelease(platformRequest).IsSuccess);
                Assert.Equal(1,spy.Calls.GetValueOrDefault("ReleaseChildExecutableArtifact"));
            }
            return;
        }
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(codeCharge + 16384UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(codeCharge, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.Equal(mode == "source-before" ? 0 : 1, spy.Calls.GetValueOrDefault("BindChildExecutableArtifact"));
    }
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static byte[] ReadActualStartPackage()
    {
        for (DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "tests", "SingPlus.Tests", "Fixtures", "OriginalNestedStart", "return42.hcexe");
            if (File.Exists(path)) return File.ReadAllBytes(path);
        }
        throw new FileNotFoundException("Actual compiler-produced nested Start package is missing.");
    }
    private static object Field(object owner, string name) => owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static object? Property(object owner, string name) => owner.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(owner);
    private static void Set(object owner, string name, object value) => owner.GetType().GetProperty(name)!.SetValue(owner, value);
    private static void Clear(object owner, string name) => ((IList)Property(owner, name)!).Clear();
}








