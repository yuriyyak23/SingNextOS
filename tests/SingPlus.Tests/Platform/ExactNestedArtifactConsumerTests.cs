using System.Collections;
using System.Reflection;
using HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
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
            var raw = base.Invoke(method, args);
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
        var spy = (ArtifactSpy)(object)native; spy.BindFault = mode == "query-reentry" ? "lost-reply" : mode;
        var rootRuntime = DispatchProxy.Create<ExactArtifactReleaseConsumerTests.IOriginalRootRuntime, ExactArtifactReleaseConsumerTests.RootSpy>();
        var rootSpy = (ExactArtifactReleaseConsumerTests.RootSpy)(object)rootRuntime; rootSpy.Inner = spy.Inner;
        var sdk = new HybridCpuExecutableChildAdapter(rootRuntime, native);
        var provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), sdk);
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 788, 1132);
        var admin = TestFixtures.Create(kernel, 99, 999).Handle;
        var budgetCap = kernel.MintCapability(new(999), admin, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "nested-artifact", [new(ServiceBudgetDimension.OwnedMemoryBytes, mode == "quota-capacity-denied" ? 24575UL : 24576UL), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 8192), new(ServiceBudgetDimension.GuestMemoryBytes, 8192), new(ServiceBudgetDimension.RegionUses, 8)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 8192).Value!;
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var root = kernel.CreateVirtualDomain(owner, create, new(1, 16384)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, root.Domain, root.ConfigureCapability).IsSuccess);
        var child = kernel.CreateNestedVirtualDomain(owner, root.Domain, root.ConfigureCapability,
            new(new(1, 8192), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.GuestMemory | VirtualDomainAuthorityClass.Execution |
                (mode == "start-terminal-query" ? VirtualDomainAuthorityClass.Traps : VirtualDomainAuthorityClass.None))).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, child.Domain, child.ConfigureCapability).IsSuccess);
        var memory = kernel.MintCapability(process.DomainId, owner, ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Execute).Value!.CapabilityId;
        var mapped = kernel.MapGuestRegion(owner, child.Domain, child.MemoryCapability, memory, region.Handle, new(0, 8192), GuestMemoryAccess.Read | GuestMemoryAccess.Execute,
            mode == "quota-missing" ? 0UL : mode == "quota-invalid" ? 16385UL : 16384UL);
        if (mode is "quota-capacity-denied" or "quota-invalid")
        {
            Assert.False(mapped.IsSuccess);
            Assert.Equal(0, spy.Calls.GetValueOrDefault("MapChildGuestMemory"));
            Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
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
                Assert.Equal(24576UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
                reservations[key!] = original;
            }
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.All(kernel.QueryBudget(budget).Value!.Usage, usage => Assert.Equal(0UL, usage.Used));
            return;
        }
        var parentHistory = (IList)spy.NativeParent.GetType().GetProperty("OriginalNestedChildren")!.GetValue(spy.NativeParent)!;
        spy.Before = name =>
        {
            if (name == "BindChildExecutableArtifact")
                Assert.Equal(24576UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
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
                    var mapping = ((IDictionary)Field(sdk, "originalMappings")).Values.Cast<object>().Single();
                    var fact = Property(mapping, "ArtifactBind")!;
                    Assert.NotNull(Property(fact, "Observation"));
                };
                if (mode == "source-aba") rootSpy.FeatureGenerationOverride = spy.Inner.QueryFeatures().Generation + 1;
                else rootSpy.ThrowSource = true;
            }
            if (mode == "sdk-history") Clear(((IDictionary)Field(sdk, "originalChildren")).Values.Cast<object>().Single(x => Property(x, "NestedParent") is null), "OriginalNested");
            if (mode is "sdk-child-aba" or "sdk-revision-exhaustion")
            {
                var originalChild = ((IDictionary)Field(sdk, "originalChildren")).Values.Cast<object>().Single(x => Property(x, "NestedParent") is not null);
                if (mode == "sdk-revision-exhaustion") originalChild.GetType().GetField("_stateRevision", Private)!.SetValue(originalChild, long.MaxValue);
                originalChild.GetType().GetProperty("State")!.SetValue(originalChild, NeutralChildDomainState.Parked);
                originalChild.GetType().GetProperty("State")!.SetValue(originalChild, NeutralChildDomainState.Created);
            }
            if (mode == "native-parent-copy")
            {
                var admission = Property(spy.NativeParent, "OriginalAdmission")!;
                var copy = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(admission, null);
                spy.NativeParent.GetType().GetProperty("OriginalAdmission")!.SetValue(spy.NativeParent, copy);
            }
            if (mode == "provider-history") Clear(((IDictionary)Field(provider, "_originalProviderChildren")).Values.Cast<object>().Single(x => Property(x, "NestedParent") is null), "OriginalNested");
            if (mode == "bridge-aba")
            {
                var parent = ((IDictionary)Field(kernel.PlatformAuthority, "_originalChildOwners")).Values.Cast<object>().Single(x => Property(x, "ParentChild") is null);
                parent.GetType().GetProperty("State")!.SetValue(parent, global::SingPlus.Platform.PlatformChildDomainState.Draining);
                parent.GetType().GetProperty("State")!.SetValue(parent, global::SingPlus.Platform.PlatformChildDomainState.Created);
            }
        };
        var bound = kernel.BindVirtualDomainExecutable(owner, child.Domain, child.ExecuteCapability, mapped.Value!.Mapping,
            mode.StartsWith("start-") ? ReadActualStartPackage() : ExecutableAdapterStartAdmissionTests.CreateExecutablePackage(), mode.StartsWith("start-") ? 512 : 3);
        Assert.Equal(mode.StartsWith("start-") || mode is "exact" or "independent-revoke" or "release-lost-reply" or "partial-consumption", bound.IsSuccess);
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
        var bridgeParents = (IDictionary)Field(kernel.PlatformAuthority, "_originalChildOwners");
        var rootOwner = bridgeParents.Values.Cast<object>().Single(x => Property(x, "ParentChild") is null);
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
                    var ownLeaf = ((IDictionary)Field(sdk, "originalChildren")).Values.Cast<object>().Single(x => Property(x, "NestedParent") is not null);
                    if (mode == "start-sdk-exhaustion") ownLeaf.GetType().GetField("_stateRevision", Private)!.SetValue(ownLeaf, long.MaxValue);
                    Set(ownLeaf, "State", NeutralChildDomainState.Parked);
                    Set(ownLeaf, "State", NeutralChildDomainState.Created);
                }
                if (mode is "start-source-aba" or "start-source-loss") rootSpy.BeforeQuery = () =>
                {
                    var ownArtifact = ((IDictionary)Field(sdk, "originalArtifacts")).Values.Cast<object>().Single();
                    Assert.NotNull(Property(Property(ownArtifact, "OriginalStart")!, "Observation"));
                    Assert.False(Monitor.IsEntered(Field(sdk, "sync")));
                    if (mode == "start-source-aba") rootSpy.FeatureGenerationOverride = spy.Inner.QueryFeatures().Generation + 1;
                    else rootSpy.ThrowSource = true;
                };
                if (mode == "start-reentry")
                    Assert.False(kernel.PlatformAuthority.TransitionChildDomain((PlatformChildBinding)Property(rootOwner, "Binding")!, global::SingPlus.Platform.PlatformChildDomainTransition.BeginDrain).IsSuccess);
                if (mode == "start-bridge-aba")
                {
                    var ancestor = ((IDictionary)Field(kernel.PlatformAuthority, "_originalChildOwners")).Values.Cast<object>().Single(x => Property(x, "ParentChild") is null);
                    Set(ancestor, "State", global::SingPlus.Platform.PlatformChildDomainState.Draining);
                    Set(ancestor, "State", global::SingPlus.Platform.PlatformChildDomainState.Created);
                }
            };
            var started = kernel.StartVirtualDomain(owner, child.Domain, child.ExecuteCapability);
            Assert.True(started.IsSuccess == (mode is "start-exact" or "start-reentry" or "start-terminal-query"), started.Message);
            if (mode != "start-no-effect") Assert.False(kernel.StartVirtualDomain(owner, child.Domain, child.ExecuteCapability).IsSuccess);
            Assert.Equal(1, spy.Calls.GetValueOrDefault("StartOriginalChildExecution"));
            var leaf = ((IDictionary)Field(spy.Inner, "childRegistry")).Values.Cast<object>().Single(x => Property(x, "ImmediateParent") is not null);
            Assert.Equal(mode is "start-native-before" or "start-no-effect" ? 0UL : 1UL, (ulong)Property(leaf, "ExecutionStartsIssued")!);
            var privateData = Field(Property(leaf, "Execution")!, "memory");
            ulong allocatedData = (ulong)Property(privateData, "AllocatedDataBytes")!;
            Assert.True(allocatedData > 0);
            Assert.True(allocatedData <= 16384);
            Assert.Equal(16384UL, (ulong)Property(privateData, "MaximumDataBytes")!);
            spy.After = null;
            if (mode == "start-terminal-query")
            {
                var originalStart = Property(leaf, "OriginalExecutionStart")!;
                var nativeTerminal = Assert.IsType<ExternalOriginalExecutionTerminalObservation>(Property(originalStart, "TerminalObservation"));
                var terminal = kernel.ObserveVirtualTrap(owner, child.Domain, child.TrapCapability);
                Assert.Equal(nativeTerminal.Kind != ExternalExecutionTerminalKind.Completed, terminal.IsSuccess);
                Assert.Equal(nativeTerminal.Kind == ExternalExecutionTerminalKind.Completed ? VirtualDomainState.Completed : VirtualDomainState.Faulted,
                    kernel.QueryVirtualDomain(owner, child.Domain).Value);
                Assert.Equal(nativeTerminal.Kind == ExternalExecutionTerminalKind.Completed ? 1 : 2,
                    spy.Calls.GetValueOrDefault("ResolveOriginalExecutionTerminal"));
                Assert.Equal(1UL, (ulong)Property(leaf, "ExecutionStartsIssued")!);
            }
            var cleanup = kernel.TerminateProcess(owner);
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
                Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.GuestMemoryBytes).Used);
                Assert.False((bool)Property(privateData, "Retired")!);
                Assert.Equal(allocatedData, (ulong)Property(privateData, "AllocatedDataBytes")!);
            }
            return;
        }
        if (mode == "independent-revoke")
        {
            var parentMapping = ((IDictionary)Field(kernel.PlatformAuthority, "_mappings")).Values.Cast<object>().Single();
            parentMapping.GetType().GetProperty("LocalAuthorizationRevoked")!.SetValue(parentMapping, true);
        }
        IDictionary? charges = null; object? chargeKey = null, originalCharge = null;
        if (mode == "partial-consumption") spy.After = name =>
        {
            if (name != "ReleaseChildExecutableArtifact") return;
            charges = (IDictionary)Field(kernel, "_mappingBudgetReservations");
            chargeKey = Assert.Single(charges.Keys.Cast<object>()); originalCharge = charges[chargeKey];
            charges.Remove(chargeKey);
        };
        var nativeChildren = (IDictionary)Field(spy.Inner, "childRegistry");
        if (mode is "native-before" or "source-before") Assert.All(nativeChildren.Values.Cast<object>(), x => Assert.Null(Property(x, "Execution")));
        if (mode is "exact" or "lost-reply" or "query-reentry" or "release-lost-reply" or "partial-consumption")
        {
            var terminated = kernel.TerminateProcess(owner);
            if (mode is "release-lost-reply" or "partial-consumption")
            {
                Assert.False(terminated.IsSuccess);
                Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
                Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
                spy.BindFault = "exact"; spy.After = null;
                if (mode == "partial-consumption") charges![chargeKey!] = originalCharge;
                terminated = kernel.TerminateProcess(owner);
            }
            Assert.True(terminated.IsSuccess, terminated.Message + " Calls=" + global::System.Text.Json.JsonSerializer.Serialize(spy.Calls));
            Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.All(kernel.QueryBudget(budget).Value!.Usage, x => Assert.Equal(0UL, x.Used));
            Assert.Equal(1, spy.Calls.GetValueOrDefault("BindChildExecutableArtifact"));
            Assert.Equal(1, spy.Calls.GetValueOrDefault("ReleaseChildExecutableArtifact"));
            return;
        }
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(24576UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.Equal(mode == "source-before" ? 0 : 1, spy.Calls.GetValueOrDefault("BindChildExecutableArtifact"));
    }
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static byte[] ReadActualStartPackage()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "tests", "SingPlus.Tests", "Fixtures", "OriginalNestedStart", "return42.hcexe");
            if (File.Exists(path)) return File.ReadAllBytes(path);
        }
        throw new FileNotFoundException("Actual compiler-produced nested Start package is missing.");
    }
    private static object Field(object owner, string name) => owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static object? Property(object owner, string name) => owner.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(owner);
    private static void Set(object owner, string name, object value) => owner.GetType().GetProperty(name)!.SetValue(owner, value);
    private static void Clear(object owner, string name) => ((IList)Property(owner, name)!).Clear();
}








