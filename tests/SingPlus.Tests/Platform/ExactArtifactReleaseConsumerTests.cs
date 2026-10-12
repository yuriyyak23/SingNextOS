using System.Collections;
using System.Reflection;
using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Tests.Platform;

public sealed partial class ExactArtifactReleaseConsumerTests
{
    [Theory]
    [InlineData("child-create-lost-reply")] [InlineData("child-create-copy")]
    [InlineData("child-create-history-loss")] [InlineData("child-create-source")]
    [InlineData("child-create-history-duplicate")]
    [InlineData("child-create-known-history-loss")] [InlineData("child-create-query-throw")]
    [InlineData("child-create-adapter-owner")] [InlineData("child-create-provider-owner")]
    [InlineData("child-create-bridge-owner")] [InlineData("child-create-reentry")]
    [InlineData("child-create-independent-root")] [InlineData("child-create-exhausted-root")]
    [InlineData("child-create-no-effect")] [InlineData("child-create-no-effect-lost-reply")]
    [InlineData("child-create-partial-close")]
    [InlineData("external-parent-bind-lost-reply")]
    [InlineData("external-parent-bind-copy")] [InlineData("external-parent-bind-history-loss")]
    [InlineData("external-parent-bind-source")] [InlineData("external-parent-bind-known-history-loss")]
    [InlineData("external-parent-bind-query-throw")] [InlineData("external-parent-bind-before-invoke")]
    [InlineData("external-parent-bind-adapter-owner")] [InlineData("external-parent-bind-provider-owner")]
    [InlineData("external-parent-bind-bridge-owner")] [InlineData("external-parent-bind-foreign-source")]
    [InlineData("external-parent-bind-no-effect")] [InlineData("external-parent-bind-no-effect-lost-reply")]
    [InlineData("external-parent-bind-reentry")] [InlineData("external-parent-bind-partial-close")]
    [InlineData("external-parent-bind-prepared-no-effect")]
    [InlineData("external-parent-bind-prepared-lost-retire")]
    [InlineData("external-parent-bind-prepared-copy-retire")]
    [InlineData("external-parent-bind-prepared-history-loss")]
    [InlineData("external-parent-bind-prepared-bound")]
    [InlineData("external-parent-bind-prepared-incapable")]
    [InlineData("external-parent-bind-prepared-foreign-retire")]
    [InlineData("external-parent-bind-prepared-known-history-loss")]
    [InlineData("external-parent-bind-prepared-reentry")]
    public void UnpublishedOriginalChildCreateMustReachActualRootAndQuantitativeReclaim(string fault)
    {
        var native = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IReleaseRuntime, ReleaseSpy>();
        var spy = (ReleaseSpy)proxy; spy.Inner = native; spy.Fault = fault;
        IHybridCpuExternalRuntime externalRoot = fault == "external-parent-bind-prepared-incapable"
            ? DispatchProxy.Create<IOriginalBindOnlyRuntime, RootSpy>() : DispatchProxy.Create<IOriginalRootRuntime, RootSpy>();
        var rootSpy = (RootSpy)externalRoot; rootSpy.Inner = native;
        rootSpy.LoseBindReply = fault == "external-parent-bind-lost-reply";
        rootSpy.Fault = fault;
        var constructor = typeof(HybridCpuExecutableChildAdapter).GetConstructor(Private, null,
            [typeof(IHybridCpuExternalRuntime), typeof(IHybridCpuChildDomainRuntimeV3), typeof(ISemanticTraceSinkV1)], null)!;
        var adapter = (HybridCpuExecutableChildAdapter)constructor.Invoke([externalRoot, proxy, null]);
        var model = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(model, adapter);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 777, 1119);
        var admin = TestFixtures.Create(kernel, 99, 999).Handle;
        var cap = kernel.MintCapability(new DomainId(999), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, cap, owner, "child-create-reclaim",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 8192)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 8192).Value!;
        var root = kernel.BindPlatformDomain(owner).Value!;
        const PlatformChildAuthorityClass authority = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory;
        var bridge = kernel.PlatformAuthority;
        IDictionary? routes = null; object? key = null, original = null;
        spy.AfterCreate = () =>
        {
            if (!fault.EndsWith("owner")) return;
            var target = fault.EndsWith("adapter-owner") ? (object)adapter : fault.EndsWith("provider-owner") ? provider : bridge;
            var field = fault.EndsWith("adapter-owner") ? "parents" : "_domains";
            routes = (IDictionary)target.GetType().GetField(field, Private)!.GetValue(target)!;
            key = Assert.Single(routes.Keys.Cast<object>()); original = routes[key]!;
            routes[key] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(original, null)!;
        };
        spy.AfterCreateQuery = () =>
        {
            if (fault is "child-create-source" or "child-create-known-history-loss")
                rootSpy.BeforeQuery = () => { rootSpy.ThrowSource = true; if (fault == "child-create-known-history-loss") spy.ClearNativeChildCreateHistory(); };
            if (fault == "child-create-reentry")
            {
                Assert.False(Monitor.IsEntered(adapter.GetType().GetField("sync", Private)!.GetValue(adapter)!));
                Assert.False(bridge.CreateChildDomain(root, root.Subject, new(new(1, 8192), new(authority, authority))).IsSuccess);
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            }
        };
        if (fault.StartsWith("external-parent-bind-")) { rootSpy.AfterBind = spy.AfterCreate; spy.AfterCreate = null; }
        rootSpy.AfterBindQuery = () =>
        {
            if (fault is "external-parent-bind-source" or "external-parent-bind-known-history-loss")
                rootSpy.BeforeQuery = () => { rootSpy.ThrowSource = true; if (fault == "external-parent-bind-known-history-loss") rootSpy.ClearBindHistory(); };
            if (fault == "external-parent-bind-reentry")
            {
                Assert.False(Monitor.IsEntered(adapter.GetType().GetField("sync", Private)!.GetValue(adapter)!));
                Assert.False(bridge.CreateChildDomain(root, root.Subject, new(new(1, 8192), new(authority, authority))).IsSuccess);
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            }
        };
        var admission = bridge.CreateChildDomain(root, root.Subject, new(new(1, 8192), new(authority, authority)));
        Assert.Equal(fault is "child-create-reentry" or "external-parent-bind-reentry", admission.IsSuccess);
        var expectedCreates = fault.StartsWith("external-parent-bind-") && fault is not ("external-parent-bind-reentry" or "external-parent-bind-provider-owner" or "external-parent-bind-bridge-owner") ? 0 : 1;
        Assert.Equal(expectedCreates, spy.CreateCalls);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        spy.AfterCreate = null; spy.AfterCreateQuery = null; rootSpy.BeforeQuery = null; rootSpy.ThrowSource = false; rootSpy.LoseBindReply = false;
        rootSpy.AfterBind = null; rootSpy.AfterBindQuery = null;
        if (routes is not null) routes[key!] = original;
        if (fault == "child-create-query-throw") spy.Fault = "exact";
        if (fault == "external-parent-bind-query-throw") rootSpy.Fault = "exact";
        if (fault == "external-parent-bind-prepared-known-history-loss")
            rootSpy.AfterBindQuery = () => rootSpy.BeforeQuery = () => { rootSpy.ClearBindHistory(); rootSpy.ThrowSource = true; };
        if (fault == "external-parent-bind-prepared-reentry")
            rootSpy.AfterRetire = () =>
            {
                Assert.False(Monitor.IsEntered(adapter.GetType().GetField("sync", Private)!.GetValue(adapter)!));
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                Assert.False(bridge.CreateChildDomain(root, root.Subject, new(new(1, 8192), new(authority, authority))).IsSuccess);
            };
        if (fault is "external-parent-bind-prepared-lost-retire" or "external-parent-bind-prepared-known-history-loss")
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1, rootSpy.RetirementCalls);
            Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            rootSpy.AfterBindQuery = null; rootSpy.BeforeQuery = null; rootSpy.ThrowSource = false;
        }
        if (fault == "external-parent-bind-partial-close")
        {
            spy.Fault = "parent-lost-reply";
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            spy.Fault = "exact";
        }
        if (fault == "child-create-partial-close")
        {
            spy.Fault = "child-close-lost-reply";
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1, spy.ChildCloseCalls);
            Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            spy.Fault = "exact";
        }
        var roots = (IDictionary)bridge.GetType().GetField("_domains", Private)!.GetValue(bridge)!;
        var rootOwner = roots[root.BindingId]!;
        if (fault == "child-create-independent-root")
        { var state = rootOwner.GetType().GetProperty("AuthorityState")!; state.SetValue(rootOwner, state.GetValue(rootOwner)); }
        if (fault == "child-create-exhausted-root") rootOwner.GetType().GetField("_authorityRevisionExhausted", Private)!.SetValue(rootOwner, 1);
        if (fault is "child-create-history-loss" or "child-create-history-duplicate" or "child-create-independent-root" or "child-create-exhausted-root" or "external-parent-bind-history-loss" or "external-parent-bind-foreign-source" or "external-parent-bind-before-invoke" or "external-parent-bind-prepared-history-loss" or "external-parent-bind-prepared-incapable" or "external-parent-bind-prepared-foreign-retire")
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess); Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(expectedCreates, spy.CreateCalls);
            Assert.Equal(fault == "external-parent-bind-before-invoke" || fault.StartsWith("external-parent-bind-prepared-") ? 0 : 1, rootSpy.OriginalBindCalls);
            if (rootSpy.Foreign is { } foreign)
            { Assert.Empty(((IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(native)!).Values.Cast<object>());
              Assert.Single(((IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(foreign)!).Values.Cast<object>()); }
            Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            return;
        }
        if (spy.ExtraChildCreate is { } dependent)
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.True(native.CloseOriginalChild(new(dependent, new(new(Guid.NewGuid()), new(802))))!.NoFurtherUse);
        }
        if (admission.IsSuccess)
        {
            Assert.True(bridge.TransitionChildDomain(admission.Value!, PlatformChildDomainTransition.BeginDrain).IsSuccess);
            Assert.True(bridge.CloseChildDomain(admission.Value!).IsSuccess);
        }
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Null(spy.CallbackFailure);
        Assert.Equal(expectedCreates, spy.CreateCalls);
        if (fault.StartsWith("external-parent-bind-")) Assert.Equal(fault.StartsWith("external-parent-bind-prepared-") ? 0 : 1, rootSpy.OriginalBindCalls);
        if (fault.StartsWith("external-parent-bind-prepared-"))
        {
            Assert.Equal(1, rootSpy.RetirementCalls);
            Assert.Null(native.BindOriginalDomain(rootSpy.Prepared!));
            Assert.Null(native.BindDomain(rootSpy.Prepared!.Request).Receipt);
            Assert.Null(native.BindDomain(rootSpy.Prepared.Request with { }).Receipt);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess); // Original process incarnation already removed.
            Assert.Equal(1, rootSpy.RetirementCalls);
        }
        if (rootSpy.IndependentRoot is { } independent)
        {
            Assert.Single(((IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(native)!).Values.Cast<object>());
            Assert.True(native.CloseOriginalDomain(new(independent, new(new(Guid.NewGuid()), new(901))))!.NoFurtherUse);
        }
        if (fault == "child-create-partial-close") Assert.Equal(1, spy.ChildCloseCalls);
        Assert.Equal(0, model.ActiveBindingCount);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public interface IOriginalRootRuntime : IHybridCpuExternalRuntime, IHybridCpuOriginalDomainBindRuntimeV1, IHybridCpuOriginalDomainBindRetirementRuntimeV1 { }
    public interface IOriginalBindOnlyRuntime : IHybridCpuExternalRuntime, IHybridCpuOriginalDomainBindRuntimeV1 { }
    public interface IReleaseRuntime : IHybridCpuChildDomainRuntimeV3, IHybridCpuArtifactReleaseRuntimeV1, IHybridCpuOriginalArtifactBindRuntimeV1, IHybridCpuOriginalArtifactBindDispositionRuntimeV1, IHybridCpuOriginalDomainCloseRuntimeV1, IHybridCpuOriginalChildCloseRuntimeV1, IHybridCpuOriginalGuestUnmapRuntimeV1, IHybridCpuOriginalGuestMapRuntimeV1, IHybridCpuOriginalChildCreateRuntimeV1 { }

    [Theory]
    [InlineData("guest-map-lost-reply")] [InlineData("guest-map-copy")]
    [InlineData("guest-map-history-loss")] [InlineData("guest-map-source")]
    [InlineData("guest-map-history-duplicate")]
    [InlineData("guest-map-known-history-loss")] [InlineData("guest-map-query-throw")]
    [InlineData("guest-map-adapter-owner")] [InlineData("guest-map-provider-owner")]
    [InlineData("guest-map-bridge-owner")] [InlineData("guest-map-reentry")]
    [InlineData("guest-map-independent-child")] [InlineData("guest-map-independent-parent")]
    [InlineData("guest-map-teardown-retry")]
    [InlineData("guest-map-no-effect")] [InlineData("guest-map-no-effect-lost-reply")]
    public void UnpublishedOriginalGuestMapMustReachActualDrainAndQuantitativeReclaim(string fault)
    {
        var native = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IReleaseRuntime, ReleaseSpy>();
        var spy = (ReleaseSpy)proxy; spy.Inner = native; spy.Fault = fault;
        var root = DispatchProxy.Create<IHybridCpuExternalRuntime, RootSpy>();
        var rootSpy = (RootSpy)root; rootSpy.Inner = native;
        var constructor = typeof(HybridCpuExecutableChildAdapter).GetConstructor(Private, null,
            [typeof(IHybridCpuExternalRuntime), typeof(IHybridCpuChildDomainRuntimeV3), typeof(ISemanticTraceSinkV1)], null)!;
        var adapter = (HybridCpuExecutableChildAdapter)constructor.Invoke([root, proxy, null]);
        var model = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(model, adapter);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 777, 1119);
        var admin = TestFixtures.Create(kernel, 99, 999).Handle;
        var budgetCap = kernel.MintCapability(new DomainId(999), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "guest-map-reclaim",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 8192), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 8192)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 8192).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var cap = kernel.MintCapability(binding.Subject.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapping = kernel.MapPlatformOwnedRegionSlice(owner, binding, cap, region.Handle, 0, 8192, PlatformMemoryAccess.Read).Value!;
        const PlatformChildAuthorityClass authority = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory;
        var bridge = kernel.PlatformAuthority;
        var child = bridge.CreateChildDomain(binding, binding.Subject, new(new(1, 8192), new(authority, authority))).Value!;
        IDictionary? ownerRoutes = null; object? ownerKey = null, ownerRecord = null;
        if (fault.EndsWith("owner"))
        {
            var target = fault.EndsWith("adapter-owner") ? (object)adapter : fault.EndsWith("provider-owner") ? provider : bridge;
            var field = fault.EndsWith("adapter-owner") ? "children" : fault.EndsWith("provider-owner") ? "_providerChildren" : "_childBindings";
            ownerRoutes = (IDictionary)target.GetType().GetField(field, Private)!.GetValue(target)!;
            ownerKey = Assert.Single(ownerRoutes.Keys.Cast<object>()); ownerRecord = ownerRoutes[ownerKey]!;
            spy.AfterMap = () => ownerRoutes[ownerKey] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(ownerRecord, null)!;
        }
        spy.AfterMapQuery = () =>
        {
            if (fault == "guest-map-reentry")
            {
                Assert.False(Monitor.IsEntered(adapter.GetType().GetField("sync", Private)!.GetValue(adapter)!));
                Assert.False(bridge.MapChildGuestRegion(child, mapping.Mapping, new(0, 8192), PlatformGuestMemoryAccess.Read).IsSuccess);
                Assert.False(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).IsSuccess);
            }
            if (fault is "guest-map-source" or "guest-map-known-history-loss")
                rootSpy.BeforeQuery = () => { rootSpy.ThrowSource = true; if (fault == "guest-map-known-history-loss") spy.ClearNativeGuestMapHistory(); };
        };
        var admission = bridge.MapChildGuestRegion(child, mapping.Mapping, new(0, 8192), PlatformGuestMemoryAccess.Read);
        Assert.Equal(fault == "guest-map-reentry", admission.IsSuccess);
        var nativeMappings = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("guestMappingRegistry", Private)!.GetValue(native)!;
        Assert.Single(nativeMappings.Values.Cast<object>());
        Assert.Equal(1, spy.MapCalls);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        spy.AfterMap = null; spy.AfterMapQuery = null; rootSpy.BeforeQuery = null; rootSpy.ThrowSource = false;
        if (ownerRoutes is not null) ownerRoutes[ownerKey!] = ownerRecord;
        if (fault == "guest-map-query-throw") spy.Fault = "exact";
        if (fault.StartsWith("guest-map-independent-"))
        {
            var target = fault.EndsWith("child") ? "_originalChildOwners" : "_mappings";
            var records = (IDictionary)bridge.GetType().GetField(target, Private)!.GetValue(bridge)!;
            var record = Assert.Single(records.Values.Cast<object>());
            var property = record.GetType().GetProperty(fault.EndsWith("child") ? "State" : "ClosureState")!;
            property.SetValue(record, property.GetValue(record));
        }
        if (fault is "guest-map-copy" or "guest-map-history-loss" or "guest-map-history-duplicate" or "guest-map-independent-child" or "guest-map-independent-parent")
        {
            Assert.False(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1, spy.MapCalls);
            Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            return;
        }
        if (fault == "guest-map-teardown-retry")
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        if (admission.IsSuccess) Assert.True(bridge.UnmapChildGuestRegion(admission.Value!).IsSuccess);
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.Null(spy.CallbackFailure);
        Assert.Equal(1, spy.MapCalls);
        if (spy.ExtraGuestMap is { } dependent)
        {
            Assert.False(bridge.CloseChildDomain(child).IsSuccess);
            Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
            Assert.True(native.UnmapOriginalGuest(new(dependent, new(new(Guid.NewGuid()), new(801))))!.NoFurtherUse);
        }
        Assert.True(bridge.CloseChildDomain(child).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(0, model.ActiveBindingCount);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
    }

    [Theory]
    [InlineData("external-parent-close")]
    [InlineData("parent-lost-reply")] [InlineData("parent-copy")]
    [InlineData("parent-history-loss")] [InlineData("parent-source")]
    [InlineData("parent-owner")] [InlineData("parent-reentry")]
    [InlineData("parent-known-history-loss")] [InlineData("parent-no-effect")]
    [InlineData("parent-pre-source")] [InlineData("parent-query-throw")]
    [InlineData("child-close-lost-reply")]
    [InlineData("child-close-copy")] [InlineData("child-close-history-loss")]
    [InlineData("child-close-source")] [InlineData("child-close-owner")]
    [InlineData("child-close-query-throw")] [InlineData("child-close-reentry")]
    [InlineData("child-close-independent")] [InlineData("child-close-revision-exhaustion")]
    [InlineData("child-close-known-history-loss")]
    [InlineData("child-close-no-effect")]
    [InlineData("guest-unmap-lost-reply")]
    [InlineData("guest-unmap-copy")] [InlineData("guest-unmap-history-loss")]
    [InlineData("guest-unmap-source")] [InlineData("guest-unmap-known-history-loss")]
    [InlineData("guest-unmap-independent")] [InlineData("guest-unmap-reentry")]
    [InlineData("guest-unmap-adapter-owner")] [InlineData("guest-unmap-provider-owner")]
    [InlineData("guest-unmap-bridge-owner")] [InlineData("guest-unmap-malformed")]
    [InlineData("guest-unmap-revision-exhaustion")]
    [InlineData("guest-unmap-independent-draining")]
    [InlineData("guest-unmap-no-effect")]
    [InlineData("guest-unmap-no-effect-lost-reply")]
    [InlineData("bind-lost-reply")]
    [InlineData("bind-malformed")]
    [InlineData("bind-history-loss")]
    [InlineData("bind-native-history-duplicate")]
    [InlineData("bind-parser-no-effect")] [InlineData("bind-parser-lost-reply")]
    [InlineData("bind-parser-copy-query")] [InlineData("bind-parser-history-loss")]
    [InlineData("bind-parser-source-aba")] [InlineData("bind-parser-query-loss")]
    [InlineData("bind-parser-reentry")] [InlineData("bind-parser-partial-consumption")]
    [InlineData("bind-parser-query-generation")] [InlineData("bind-parser-query-digest")]
    [InlineData("bind-late-refusal")] [InlineData("bind-late-lost-reply")]
    [InlineData("bind-late-copy-query")] [InlineData("bind-late-history-loss")]
    [InlineData("bind-late-source-aba")] [InlineData("bind-late-reentry")]
    [InlineData("bind-native-child-admission-copy")] [InlineData("bind-native-mapping-admission-copy")]
    [InlineData("bind-query-source")] [InlineData("bind-query-bridge-owner")]
    [InlineData("bind-query-reentry")] [InlineData("bind-query-throw")]
    [InlineData("bind-independent-parent")]
    [InlineData("exact")] [InlineData("lost-reply")] [InlineData("source")]
    [InlineData("adapter-owner")] [InlineData("provider-owner")] [InlineData("bridge-owner")]
    [InlineData("reentry")] [InlineData("malformed")] [InlineData("history-loss")]
    [InlineData("pre-adapter-owner")] [InlineData("pre-provider-owner")] [InlineData("pre-bridge-owner")]
    [InlineData("absent-adapter-owner")] [InlineData("absent-provider-owner")] [InlineData("absent-bridge-owner")]
    [InlineData("pre-effect-overlap")]
    [InlineData("consumed-adapter-owner")] [InlineData("consumed-provider-owner")] [InlineData("consumed-bridge-owner")]
    [InlineData("failed-start")] [InlineData("failed-start-lost-reply")]
    [InlineData("failed-start-refusal-history-duplicate")]
    [InlineData("failed-start-independent-child")] [InlineData("failed-start-independent-guest")]
    [InlineData("failed-start-independent-parent")] [InlineData("failed-start-revision-exhaustion")]
    [InlineData("failed-start-release-independent-parent")]
    [InlineData("failed-start-release-source")] [InlineData("failed-start-release-lost-reply")]
    [InlineData("failed-start-release-history-loss")]
    [InlineData("release-request-copy")] [InlineData("release-query-request-copy")]
    [InlineData("native-release-copy")] [InlineData("native-release-bind-copy")]
    public void OriginalArtifactReleaseControlsActualBackingAndQuantitativeReclaim(string fault)
    {
        var native = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IReleaseRuntime, ReleaseSpy>();
        var spy = (ReleaseSpy)proxy; spy.Inner = native;
        var root = DispatchProxy.Create<IHybridCpuExternalRuntime, RootSpy>();
        var rootSpy = (RootSpy)root; rootSpy.Inner = native;
        var constructor = typeof(HybridCpuExecutableChildAdapter).GetConstructor(Private, null,
            [typeof(IHybridCpuExternalRuntime), typeof(IHybridCpuChildDomainRuntimeV3), typeof(ISemanticTraceSinkV1)], null)!;
        var adapter = (HybridCpuExecutableChildAdapter)constructor.Invoke([root, proxy, null]);
        var model = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(model, adapter);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 776, 1118);
        var admin = TestFixtures.Create(kernel, 98, 998).Handle;
        var budgetCap = kernel.MintCapability(new DomainId(998), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "artifact-release",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 8192), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 8192)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 8192).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var cap = kernel.MintCapability(binding.Subject.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapping = kernel.MapPlatformOwnedRegionSlice(owner, binding, cap, region.Handle, 0, 8192, PlatformMemoryAccess.Read).Value!;
        var bridge = kernel.PlatformAuthority;
        const PlatformChildAuthorityClass authority = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.Execution | PlatformChildAuthorityClass.GuestMemory;
        // Trusted bridge fixture: public RuntimeAdmission gate remains closed for Executable contour.
        var child = bridge.CreateChildDomain(binding, binding.Subject, new(new(1, 8192), new(authority, authority))).Value!;
        var guest = bridge.MapChildGuestRegion(child, mapping.Mapping, new(0, 8192), PlatformGuestMemoryAccess.Read | PlatformGuestMemoryAccess.Execute).Value!;
        spy.Fault = fault;
        bool observedOriginalBindIntent = false;
        spy.BeforeBind = externalRequest =>
        {
            var adapterOwner = Assert.Single(((IDictionary)adapter.GetType().GetField("originalMappings", Private)!.GetValue(adapter)!).Values.Cast<object>());
            var adapterIntent = adapterOwner.GetType().GetProperty("ArtifactBind")!.GetValue(adapterOwner)!;
            Assert.Same(externalRequest, adapterIntent.GetType().GetProperty("External")!.GetValue(adapterIntent));
            var providerOwner = Assert.Single(((IDictionary)provider.GetType().GetField("_originalProviderGuestMappings", Private)!.GetValue(provider)!).Values.Cast<object>());
            var providerIntent = providerOwner.GetType().GetProperty("OriginalArtifactBindInvocation")!.GetValue(providerOwner)!;
            Assert.NotNull(providerIntent);
            Assert.Null(providerIntent.GetType().GetProperty("Observation")!.GetValue(providerIntent));
            var localOwner = Assert.Single(((IDictionary)bridge.GetType().GetField("_originalExecutableArtifactOwners", Private)!.GetValue(bridge)!).Values.Cast<object>());
            var localIntent = localOwner.GetType().GetProperty("BindInvocation")!.GetValue(localOwner)!;
            Assert.True((bool)localIntent.GetType().GetProperty("Invoked")!.GetValue(localIntent)!);
            observedOriginalBindIntent = true;
        };
        var parserRefusal = fault.StartsWith("bind-parser-");
        var bindPackage = parserRefusal ? new byte[] { 1, 2, 3, 4 } : ExecutableAdapterStartAdmissionTests.CreateExecutablePackage();
        if (fault.StartsWith("bind-late-"))
        {
            var imageBase = global::System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bindPackage.AsSpan(32));
            var entry = global::System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bindPackage.AsSpan(40));
            global::System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bindPackage.AsSpan(32), 0x4000_0000);
            global::System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bindPackage.AsSpan(40), 0x4000_0000 + entry - imageBase);
            bindPackage.AsSpan(288, 32).Clear();
            global::System.Security.Cryptography.SHA256.HashData(bindPackage).CopyTo(bindPackage, 288);
        }
        var bound = bridge.BindChildExecutableArtifact(child, guest, bindPackage, 3);
        Assert.True(observedOriginalBindIntent);
        Assert.Equal(!fault.StartsWith("bind-"), bound.IsSuccess); var artifact = bound.Value;
        object target = fault.EndsWith("adapter-owner") ? adapter : fault.EndsWith("provider-owner") ? provider : bridge;
        var field = fault.EndsWith("adapter-owner") ? "artifacts" : fault.EndsWith("provider-owner") ? "_providerArtifacts" : "_executableArtifacts";
        var routes = (IDictionary)target.GetType().GetField(field, Private)!.GetValue(target)!;
        var key = Assert.Single(routes.Keys.Cast<object>()); var original = routes[key]!;
        if (fault.StartsWith("bind-"))
        {
            artifact = (PlatformExecutableArtifactBinding)original.GetType().GetProperty("Binding")!.GetValue(original)!;
            Assert.False(bridge.BindChildExecutableArtifact(child, guest, ExecutableAdapterStartAdmissionTests.CreateExecutablePackage(), 3).IsSuccess);
            Assert.Equal(1, spy.BindCalls);
        }
        var gate = target.GetType().GetField(target == adapter ? "sync" : target == provider ? "_childCreateCommitGate" : "_secureDomainLifecycleGate", Private)!.GetValue(target)!;
        bool preRefusal = fault.StartsWith("pre-") || fault.StartsWith("absent-");
        if (preRefusal)
        {
            if (fault.StartsWith("absent-")) routes.Remove(key);
            else routes[key] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(original, null)!;
            Assert.False(bridge.UnmapChildGuestRegion(guest).IsSuccess);
            Assert.Equal(0, spy.ReleaseCalls); Assert.Equal(0, spy.UnmapCalls);
            routes[key] = original;
        }
        spy.AfterRelease = () =>
        {
            Assert.False(Monitor.IsEntered(gate));
            if (fault is "source" or "failed-start-release-source") rootSpy.ThrowSource = true;
            if (fault == "failed-start-release-independent-parent") ChangeDependencyRevision();
            if (!preRefusal && !fault.StartsWith("guest-unmap-") && !fault.StartsWith("child-close-") && !fault.StartsWith("parent-") && !fault.StartsWith("consumed-") && fault.EndsWith("owner")) routes[key] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(original, null)!;
            if (fault == "reentry")
            {
                Assert.False(bridge.UnmapChildGuestRegion(guest).IsSuccess);
                Assert.False(bridge.StartChildExecutableArtifact(artifact).IsSuccess);
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            }
        };
        spy.Fault = fault;
        bool independentBefore = fault.StartsWith("failed-start-independent-") || fault is "failed-start-revision-exhaustion" or "bind-independent-parent";
        void ChangeDependencyRevision()
        {
            var dependencyField = fault.EndsWith("child") ? "_childBindings" : fault.EndsWith("guest") ? "_guestBindings" : "_mappings";
            object dependencyKey = dependencyField == "_childBindings" ? child.BindingId : dependencyField == "_guestBindings" ? guest.MappingId : mapping.Mapping.MappingId;
            var dependencies = (IDictionary)bridge.GetType().GetField(dependencyField, Private)!.GetValue(bridge)!;
            var dependency = dependencies[dependencyKey]!;
            var state = dependency.GetType().GetProperty(dependencyField == "_childBindings" ? "State" : dependencyField == "_guestBindings" ? "Closure" : "ClosureState")!;
            if (fault == "failed-start-revision-exhaustion") dependency.GetType().GetField("_lifecycleRevision", Private)!.SetValue(dependency, long.MaxValue);
            state.SetValue(dependency, state.GetValue(dependency)); // Same projection, independent mutation/lifetime observation.
        }
        if (fault.StartsWith("failed-start"))
        {
            Assert.False(bridge.StartChildExecutableArtifact(artifact).IsSuccess);
            if (independentBefore) ChangeDependencyRevision();
        }
        if (fault == "bind-independent-parent") ChangeDependencyRevision();
        spy.AfterBindQuery = () =>
        {
            if (fault is "bind-late-source-aba" or "bind-late-reentry" or "bind-parser-source-aba" or "bind-parser-reentry" or "bind-parser-partial-consumption")
                rootSpy.BeforeQuery = () =>
                {
                    rootSpy.BeforeQuery = null;
                    var nativeFact = Assert.Single(((IDictionary)adapter.GetType().GetField("originalMappings", Private)!.GetValue(adapter)!).Values.Cast<object>());
                    var retained = nativeFact.GetType().GetProperty("ArtifactBind")!.GetValue(nativeFact)!;
                    var disposition = (ExternalOriginalArtifactBindDisposition)retained.GetType().GetProperty("Disposition")!.GetValue(retained)!;
                    Assert.Equal(!fault.StartsWith("bind-late-"), disposition.NoEffect);
                    if (fault.StartsWith("bind-late-")) Assert.Same(disposition.CleanupArtifact, retained.GetType().GetProperty("ResolutionObservation")!.GetValue(retained));
                    foreach (var (component, lockName) in new[] { ((object)bridge, "_secureDomainLifecycleGate"), ((object)provider, "_childCreateCommitGate"), ((object)adapter, "sync") })
                        Assert.False(Monitor.IsEntered(component.GetType().GetField(lockName, Private)!.GetValue(component)!));
                    if (fault is "bind-parser-source-aba" or "bind-late-source-aba") rootSpy.FeatureGenerationOverride = native.QueryFeatures().Generation + 1;
                    if (fault is "bind-parser-reentry" or "bind-late-reentry") Assert.False(bridge.UnmapChildGuestRegion(guest).IsSuccess);
                    if (fault == "bind-parser-partial-consumption")
                    {
                        var guestOwner = ((IDictionary)bridge.GetType().GetField("_originalGuestOwners", Private)!.GetValue(bridge)!)[guest.MappingId]!;
                        guestOwner.GetType().GetProperty("PendingSecureRegionEffects")!.SetValue(guestOwner, 1);
                    }
                };
            foreach (var (owner, fieldName) in new[] { ((object)bridge, "_secureDomainLifecycleGate"), ((object)provider, "_childCreateCommitGate"), ((object)adapter, "sync") })
                Assert.False(Monitor.IsEntered(owner.GetType().GetField(fieldName, Private)!.GetValue(owner)!));
            if (fault == "bind-query-source") rootSpy.ThrowSource = true;
            if (fault == "bind-query-bridge-owner") routes[key] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(original, null)!;
            if (fault == "bind-query-reentry")
            {
                Assert.Equal(KernelError.PlatformBindingActive, bridge.UnmapChildGuestRegion(guest).Error);
                Assert.Equal(KernelError.PlatformFaulted, bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);
                Assert.False(bridge.StartChildExecutableArtifact(artifact).IsSuccess);
                Assert.Equal(1, spy.BindCalls);
            }
            if (fault == "bind-query-throw") throw new IOException("Pure original lookup reply lost.");
        };
        if (fault == "pre-effect-overlap") rootSpy.BeforeQuery = () =>
        {
            rootSpy.BeforeQuery = null;
            var adapterRoutes = (IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("artifacts", Private)!.GetValue(adapter)!;
            var adapterKey = Assert.Single(adapterRoutes.Keys.Cast<object>()); var adapterOriginal = adapterRoutes[adapterKey]!;
            var nativeRequest = (NeutralArtifactReleaseRequest)adapterOriginal.GetType().GetProperty("ReleaseRequest")!.GetValue(adapterOriginal)!;
            adapterRoutes[adapterKey] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(adapterOriginal, null)!;
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.ResolveExecutableArtifactRelease(nativeRequest).Status);
            adapterRoutes[adapterKey] = adapterOriginal;
            var providerRoutes = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_providerArtifacts", Private)!.GetValue(provider)!;
            var providerKey = Assert.Single(providerRoutes.Keys.Cast<object>()); var providerOriginal = providerRoutes[providerKey]!;
            var platformRequest = (PlatformArtifactReleaseRequest)providerOriginal.GetType().GetProperty("ReleaseRequest")!.GetValue(providerOriginal)!;
            providerRoutes[providerKey] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(providerOriginal, null)!;
            Assert.Equal(PlatformAuthorityStatus.Faulted, provider.ResolveExecutableArtifactRelease(platformRequest).Status);
            providerRoutes[providerKey] = providerOriginal;
            Assert.Equal(0, spy.ReleaseCalls);
        };
        IDictionary? unmapRoutes = null; object? unmapKey = null, unmapOriginal = null;
        if (fault.StartsWith("guest-unmap-") && fault.EndsWith("owner"))
        {
            var unmapTarget = fault.EndsWith("adapter-owner") ? (object)adapter : fault.EndsWith("provider-owner") ? provider : bridge;
            var unmapField = fault.EndsWith("adapter-owner") ? "mappings" : fault.EndsWith("provider-owner") ? "_providerGuestMappings" : "_guestBindings";
            unmapRoutes = (IDictionary)unmapTarget.GetType().GetField(unmapField, Private)!.GetValue(unmapTarget)!;
            unmapKey = Assert.Single(unmapRoutes.Keys.Cast<object>()); unmapOriginal = unmapRoutes[unmapKey]!;
            spy.AfterGuestUnmap = () => unmapRoutes[unmapKey] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(unmapOriginal, null)!;
        }
        spy.AfterGuestQuery = () =>
        {
            if (fault == "guest-unmap-independent-draining")
            {
                var guestOwners = (IDictionary)bridge.GetType().GetField("_originalGuestOwners", Private)!.GetValue(bridge)!;
                var originalGuest = Assert.Single(guestOwners.Values.Cast<object>());
                originalGuest.GetType().GetProperty("Closure")!.SetValue(originalGuest, PlatformExternalClosureState.Draining);
            }
            if (fault == "guest-unmap-reentry")
            {
                Assert.False(Monitor.IsEntered(adapter.GetType().GetField("sync", Private)!.GetValue(adapter)!));
                Assert.False(bridge.UnmapChildGuestRegion(guest).IsSuccess);
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            }
            if (fault is "guest-unmap-source" or "guest-unmap-known-history-loss")
                rootSpy.BeforeQuery = () =>
                {
                    rootSpy.ThrowSource = true;
                    if (fault == "guest-unmap-known-history-loss") spy.ClearNativeGuestUnmapHistory();
                };
        };
        if (fault == "guest-unmap-revision-exhaustion")
        {
            var guestOwners = (IDictionary)bridge.GetType().GetField("_originalGuestOwners", Private)!.GetValue(bridge)!;
            Assert.Single(guestOwners.Values.Cast<object>()).GetType().GetField("_lifecycleRevision", Private)!.SetValue(Assert.Single(guestOwners.Values.Cast<object>()), long.MaxValue);
        }
        var first = bridge.UnmapChildGuestRegion(guest);
        Assert.Null(spy.CallbackFailure);
        bool startImmediate = fault is "failed-start" or "failed-start-lost-reply" or "failed-start-refusal-history-duplicate";
        bool bindNoEffect = fault is "bind-parser-no-effect" or "bind-parser-lost-reply" or "bind-parser-reentry";
        bool bindPinned = fault is "bind-late-source-aba" or "bind-late-copy-query" or "bind-late-history-loss" or "bind-parser-source-aba" or "bind-parser-query-loss" or "bind-parser-query-generation" or "bind-parser-query-digest" or "bind-parser-copy-query" or "bind-parser-history-loss" or "bind-native-child-admission-copy" or "bind-native-mapping-admission-copy" or "bind-malformed" or "bind-history-loss" or "bind-native-history-duplicate";
        bool bindQueryBlocked = fault is "bind-query-source" or "bind-query-bridge-owner" or "bind-query-throw";
        Assert.Equal(bindNoEffect || preRefusal || fault.StartsWith("child-close-") || fault.StartsWith("parent-") || fault.StartsWith("consumed-") || startImmediate || fault is "bind-late-reentry" or "bind-late-refusal" or "bind-late-lost-reply" or "exact" or "reentry" or "pre-effect-overlap" or "bind-lost-reply" or "bind-query-reentry" or "external-parent-close" or "guest-unmap-reentry", first.IsSuccess);
        Assert.Equal(parserRefusal || independentBefore || bindPinned || bindQueryBlocked ? 0 : 1, spy.ReleaseCalls);
        if (fault == "failed-start-release-history-loss") Assert.True(spy.NativeHistoryLost);
        Assert.Equal(first.IsSuccess || fault.StartsWith("guest-unmap-") ? 1 : 0, spy.UnmapCalls);
        if (fault.StartsWith("guest-unmap-") && !first.IsSuccess)
        {
            var nativeMappings = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("guestMappingRegistry", Private)!.GetValue(native)!;
            var nativeMapping = Assert.Single(nativeMappings.Values.Cast<object>());
            Assert.True((bool)nativeMapping.GetType().GetProperty("Closed")!.GetValue(nativeMapping)!);
            Assert.Equal(!fault.StartsWith("guest-unmap-no-effect"), nativeMapping.GetType().GetProperty("OriginalClosure")!.GetValue(nativeMapping) is not null);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1, spy.UnmapCalls);
            if (fault == "guest-unmap-independent")
            {
                var guests = (IDictionary)bridge.GetType().GetField("_originalGuestOwners", Private)!.GetValue(bridge)!;
                var ownerGuest = Assert.Single(guests.Values.Cast<object>());
                ownerGuest.GetType().GetProperty("Closure")!.SetValue(ownerGuest, PlatformExternalClosureState.Faulted);
            }
        }
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        spy.AfterRelease = null; spy.AfterBindQuery = null; spy.AfterGuestQuery = null; rootSpy.BeforeQuery = null; rootSpy.ThrowSource = false; routes[key] = original;
        if (fault is "bind-parser-source-aba" or "bind-late-source-aba") rootSpy.FeatureGenerationOverride = null;
        if (fault == "bind-parser-partial-consumption")
        {
            var guestOwner = ((IDictionary)bridge.GetType().GetField("_originalGuestOwners", Private)!.GetValue(bridge)!)[guest.MappingId]!;
            guestOwner.GetType().GetProperty("PendingSecureRegionEffects")!.SetValue(guestOwner, 0);
            Assert.True((bool)original.GetType().GetProperty("Released")!.GetValue(original)!);
            Assert.Equal(1, spy.DispositionQueries);
        }
        spy.AfterGuestUnmap = null;
        if (unmapRoutes is not null) unmapRoutes[unmapKey!] = unmapOriginal;
        if (fault.StartsWith("guest-unmap-no-effect"))
        {
            var nativeMappings = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("guestMappingRegistry", Private)!.GetValue(native)!;
            var nativeMapping = Assert.Single(nativeMappings.Values.Cast<object>());
            nativeMapping.GetType().GetProperty("Closed")!.SetValue(nativeMapping, false);
        }
        if (fault.StartsWith("consumed-"))
        {
            routes[key] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(original, null)!;
            Assert.True(bridge.UnmapChildGuestRegion(guest, allowCommittedClosure: true).IsSuccess);
        }
        bool pinned = independentBefore || bindPinned || fault is "native-release-copy" or "native-release-bind-copy" or "release-request-copy" or "release-query-request-copy" or "malformed" or "history-loss" or "failed-start-release-independent-parent" or "failed-start-release-history-loss" or "guest-unmap-history-loss" or "guest-unmap-independent" or "guest-unmap-independent-draining" or "guest-unmap-revision-exhaustion";
        if (fault == "guest-unmap-no-effect-lost-reply")
        {
            Assert.False(bridge.UnmapChildGuestRegion(guest).IsSuccess);
            Assert.Equal(1, spy.UnmapCalls);
            Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        if (!first.IsSuccess) Assert.Equal(!pinned, bridge.UnmapChildGuestRegion(guest).IsSuccess);
        if (fault == "bind-parser-partial-consumption") Assert.Equal(1, spy.DispositionQueries);
        if (fault.StartsWith("guest-unmap-")) Assert.Equal(fault.StartsWith("guest-unmap-no-effect") ? 2 : 1, spy.UnmapCalls);
        Assert.Equal(parserRefusal || independentBefore || bindPinned ? 0 : 1, spy.ReleaseCalls);
        Assert.False(bridge.StartChildExecutableArtifact(artifact).IsSuccess);
        Assert.Equal(fault.StartsWith("failed-start") ? 1 : 0, spy.StartCalls);
        var localArtifact = ((IDictionary)bridge.GetType().GetField("_executableArtifacts", Private)!.GetValue(bridge)!)[artifact.BindingId]!;
        Assert.False((bool)localArtifact.GetType().GetProperty("ReleaseInFlight")!.GetValue(localArtifact)!);
        if (pinned)
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
            Assert.Equal(parserRefusal || independentBefore || bindPinned ? 0 : 1, spy.ReleaseCalls);
            return;
        }
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        if (fault.StartsWith("child-close-"))
        {
            var adapterChildren = (IDictionary)adapter.GetType().GetField("children", Private)!.GetValue(adapter)!;
            var adapterChildKey = Assert.Single(adapterChildren.Keys.Cast<object>());
            var adapterOriginalChild = adapterChildren[adapterChildKey]!;
            ExternalGuestMappingLease? independentNativeMapping = null;
            if (fault == "child-close-no-effect")
            {
                var nativeChildLease = (ExternalChildDomainLease)adapterOriginalChild.GetType().GetProperty("External")!.GetValue(adapterOriginalChild)!;
                independentNativeMapping = native.MapChildGuestMemory(nativeChildLease, new(0, 4096, new(new(Guid.NewGuid()), new(900)))).Receipt!.Mapping;
            }
            spy.AfterChildClose = () =>
            {
                foreach (var (component, field) in new[] { ((object)bridge, "_secureDomainLifecycleGate"), ((object)provider, "_childCreateCommitGate"), ((object)adapter, "sync") })
                    Assert.False(Monitor.IsEntered(component.GetType().GetField(field, Private)!.GetValue(component)!));
                if (fault == "child-close-owner") adapterChildren[adapterChildKey] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(adapterOriginalChild, null)!;
                if (fault == "child-close-reentry")
                { Assert.False(bridge.CloseChildDomain(child).IsSuccess); Assert.False(kernel.TerminateProcess(owner).IsSuccess); }
            };
            spy.AfterChildQuery = () =>
            {
                if (fault == "child-close-query-throw") throw new IOException("pure child closure lookup interrupted");
                if (fault is "child-close-source" or "child-close-known-history-loss")
                    rootSpy.BeforeQuery = () =>
                    {
                        rootSpy.ThrowSource = true;
                        if (fault == "child-close-known-history-loss") spy.ClearNativeChildCloseHistory();
                    };
            };
            var bridgeChild = ((IDictionary)bridge.GetType().GetField("_childBindings", Private)!.GetValue(bridge)!)[child.BindingId]!;
            if (fault == "child-close-revision-exhaustion") bridgeChild.GetType().GetField("_lifecycleRevision", Private)!.SetValue(bridgeChild, long.MaxValue);
            var childClosed = bridge.CloseChildDomain(child);
            Assert.Null(spy.CallbackFailure);
            Assert.Equal(fault == "child-close-reentry", childClosed.IsSuccess);
            var nativeChildren = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(native)!;
            var originalChild = Assert.Single(nativeChildren.Values.Cast<object>());
            Assert.Equal(fault == "child-close-no-effect" ? ExternalChildDomainState.Ready : ExternalChildDomainState.Closed, originalChild.GetType().GetProperty("State")!.GetValue(originalChild));
            Assert.Equal(fault != "child-close-no-effect", originalChild.GetType().GetProperty("OriginalClosure")!.GetValue(originalChild) is not null);
            if (fault == "child-close-independent")
            {
                var state = bridgeChild.GetType().GetProperty("State")!;
                state.SetValue(bridgeChild, state.GetValue(bridgeChild));
            }
            bool childPinned = fault is "child-close-history-loss" or "child-close-independent" or "child-close-revision-exhaustion";
            spy.AfterChildClose = null; spy.AfterChildQuery = null; rootSpy.BeforeQuery = null; rootSpy.ThrowSource = false; adapterChildren[adapterChildKey] = adapterOriginalChild;
            if (independentNativeMapping is { } nativeMapping)
            {
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                Assert.Equal(ExternalRuntimeOutcome.Closed, native.UnmapChildGuestMemory(nativeMapping, new(new(Guid.NewGuid()), new(901))).Outcome);
            }
            if (childPinned)
            {
                Assert.False(bridge.CloseChildDomain(child).IsSuccess);
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                Assert.Equal(1, spy.ChildCloseCalls);
                Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
                Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
                return;
            }
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            if (childClosed.IsSuccess) Assert.False(bridge.CloseChildDomain(child).IsSuccess);
            else Assert.True(bridge.CloseChildDomain(child).IsSuccess);
            Assert.Equal(fault == "child-close-no-effect" ? 2 : 1, spy.ChildCloseCalls);
        }
        else Assert.True(bridge.CloseChildDomain(child).IsSuccess);
        var parentRoutes = (IDictionary)adapter.GetType().GetField("parents", Private)!.GetValue(adapter)!;
        var parentKey = Assert.Single(parentRoutes.Keys.Cast<object>());
        var parentOriginal = parentRoutes[parentKey]!;
        ExternalChildDomainLease? extraChild = null;
        if (fault == "parent-no-effect")
        {
            var nativeParent = (ExternalDomainLease)parentOriginal.GetType().GetProperty("External")!.GetValue(parentOriginal)!;
            extraChild = native.CreateChildDomain(nativeParent, new(Guid.NewGuid(), ExternalChildAuthority.Execute, 4096, new(new(Guid.NewGuid()), new(400)))).Receipt!.Lease;
        }
        spy.AfterParentClose = () =>
        {
            foreach (var (component, field) in new[] { ((object)bridge, "_secureDomainLifecycleGate"), ((object)provider, "_childCreateCommitGate"), ((object)adapter, "sync") })
                Assert.False(Monitor.IsEntered(component.GetType().GetField(field, Private)!.GetValue(component)!));
            if (fault == "parent-owner") parentRoutes[parentKey] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(parentOriginal, null)!;
            if (fault == "parent-reentry") Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        };
        spy.AfterParentQuery = () =>
        {
            if (fault == "parent-query-throw") throw new IOException("pure original lookup interrupted");
            if (fault is "parent-source" or "parent-known-history-loss")
            {
                // The next mutable-source callback follows canonical proof retention.
                rootSpy.BeforeQuery = () =>
                {
                    rootSpy.ThrowSource = true;
                    if (fault == "parent-known-history-loss") ClearNativeParentHistory();
                };
            }
        };
        void ClearNativeParentHistory()
        {
            var nativeParents = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(native)!;
            Assert.Single(nativeParents.Values.Cast<object>()).GetType().GetProperty("OriginalClose")!.SetValue(Assert.Single(nativeParents.Values.Cast<object>()), null);
        }
        if (fault == "parent-pre-source") rootSpy.ThrowSource = true;
        var terminal = kernel.TerminateProcess(owner);
        Assert.Null(spy.CallbackFailure);
        if (fault.StartsWith("parent-") && fault != "parent-reentry")
        {
            Assert.False(terminal.IsSuccess);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(fault == "parent-pre-source" ? 0 : 1, spy.ParentCloseCalls);
            spy.AfterParentClose = null; spy.AfterParentQuery = null; rootSpy.BeforeQuery = null; rootSpy.ThrowSource = false; parentRoutes[parentKey] = parentOriginal;
            if (fault == "parent-pre-source")
            {
                var originalRoots = (IDictionary)bridge.GetType().GetField("_domains", Private)!.GetValue(bridge)!;
                var rootOwner = originalRoots[binding.BindingId]!;
                var providerLease = (PlatformProviderDomainLease)rootOwner.GetType().GetProperty("ProviderLease")!.GetValue(rootOwner)!;
                Assert.False(provider.ResolveDomainClosure(providerLease).IsSuccess);
                Assert.Equal(0, spy.ParentCloseCalls); // Pure lookup cannot create the remaining effect.
            }
            if (extraChild is { } dependent) Assert.Equal(ExternalRuntimeOutcome.Closed, native.CloseChildDomain(dependent, new(new(Guid.NewGuid()), new(401))).Outcome);
            terminal = kernel.TerminateProcess(owner);
            Assert.Equal(fault == "parent-no-effect" ? 2 : 1, spy.ParentCloseCalls);
            Assert.False((bool)parentOriginal.GetType().GetProperty("CloseInFlight")!.GetValue(parentOriginal)!);
            if (fault == "parent-history-loss")
            {
                Assert.False(terminal.IsSuccess);
                Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
                return;
            }
        }
        Assert.True(terminal.IsSuccess, terminal.Message);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.Equal(0, model.ActiveBindingCount);
        if (fault == "external-parent-close" || fault.StartsWith("parent-"))
        {
            var nativeParents = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(native)!;
            Assert.All(nativeParents.Values.Cast<object>(), parentOwner =>
                Assert.Equal("Closed", parentOwner.GetType().GetProperty("Lifecycle")!.GetValue(parentOwner)!.ToString()));
        }
    }

    public class RootSpy : DispatchProxy
    {
        public IHybridCpuExternalRuntime Inner = null!;
        public bool ThrowSource;
        public ulong? FeatureGenerationOverride;
        public bool LoseBindReply;
        public Action? BeforeQuery;
        public string Fault = "exact";
        public Action? AfterBind, AfterBindQuery;
        public Action? AfterRetire;
        public int OriginalBindCalls;
        public int RetirementCalls;
        public HybridCpuExternalRuntime? Foreign;
        public ExternalDomainBindReceipt? IndependentRoot;
        public ExternalOriginalDomainBindRequest? Prepared;
        public void ClearBindHistory() => ((IDictionary)typeof(HybridCpuExternalRuntime).GetField("originalDomainBinds", Private)!.GetValue(Inner)!).Clear();
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IHybridCpuExternalRuntime.QueryFeatures))
            {
                BeforeQuery?.Invoke();
                if (ThrowSource) throw new IOException("source loss");
            }
            if (method.Name == nameof(IHybridCpuOriginalDomainBindRuntimeV1.PrepareOriginalDomainBind) && Fault == "external-parent-bind-before-invoke")
                throw new IOException("Pure preparation interrupted before Bind invocation");
            if (method.Name == nameof(IHybridCpuOriginalDomainBindRuntimeV1.PrepareOriginalDomainBind) && Fault.StartsWith("external-parent-bind-no-effect"))
                IndependentRoot = Inner.BindDomain(((ExternalDomainBindRequest)args![0]!) with { RequestId = Guid.NewGuid() }).Receipt!;
            if (method.Name == nameof(IHybridCpuOriginalDomainBindRetirementRuntimeV1.RetireOriginalDomainBind))
            {
                RetirementCalls++;
                if (Fault == "external-parent-bind-prepared-bound")
                    Assert.NotNull(((IHybridCpuOriginalDomainBindRuntimeV1)Inner).BindOriginalDomain(Prepared!)!.Raw.Receipt);
            }
            object target = Inner;
            if (method.Name == nameof(IHybridCpuOriginalDomainBindRetirementRuntimeV1.RetireOriginalDomainBind) && Fault == "external-parent-bind-prepared-foreign-retire")
            {
                Foreign = new();
                IndependentRoot = Foreign.BindDomain(new(Guid.NewGuid(), ExternalDomainProfile.IsolatedDomain, new(new(Guid.NewGuid()), new(900)))).Receipt!;
                target = Foreign;
            }
            if (method.Name == nameof(IHybridCpuOriginalDomainBindRuntimeV1.BindOriginalDomain))
            {
                OriginalBindCalls++;
                var prepared = (ExternalOriginalDomainBindRequest)args![0]!;
                if (Fault == "external-parent-bind-foreign-source")
                { Foreign = new(); IndependentRoot = Foreign.BindDomain(new(Guid.NewGuid(), ExternalDomainProfile.IsolatedDomain, new(new(Guid.NewGuid()), new(900)))).Receipt!; target = Foreign; }
            }
            var result = method!.Invoke(target, args);
            if (method.Name == nameof(IHybridCpuExternalRuntime.QueryFeatures) && FeatureGenerationOverride is { } generation && result is HybridCpuExternalFeatureManifest manifest)
                result = new HybridCpuExternalFeatureManifest(manifest.ContractVersion,generation,manifest.Features);
            if (method.Name == nameof(IHybridCpuOriginalDomainBindRuntimeV1.PrepareOriginalDomainBind))
            {
                Prepared = (ExternalOriginalDomainBindRequest?)result;
                if (Fault.StartsWith("external-parent-bind-prepared-")) BeforeQuery = () => ThrowSource = true;
            }
            if (method.Name == nameof(IHybridCpuOriginalDomainBindRetirementRuntimeV1.RetireOriginalDomainBind))
            {
                AfterRetire?.Invoke();
                if (Fault == "external-parent-bind-prepared-history-loss") ClearBindHistory();
                if (Fault is "external-parent-bind-prepared-lost-retire" or "external-parent-bind-prepared-history-loss") throw new IOException("Original retirement reply lost");
                if (Fault == "external-parent-bind-prepared-copy-retire" && result is ExternalOriginalDomainBindObservation retired) return retired with { };
            }
            if (method.Name == nameof(IHybridCpuOriginalDomainBindRuntimeV1.ResolveOriginalDomainBind))
            {
                if (Fault == "external-parent-bind-query-throw") throw new IOException("Original Bind lookup interrupted");
                AfterBindQuery?.Invoke();
            }
            if (method.Name == nameof(IHybridCpuOriginalDomainBindRuntimeV1.BindOriginalDomain))
            {
                AfterBind?.Invoke();
                if (Fault == "external-parent-bind-history-loss") ClearBindHistory();
                if (Fault is "external-parent-bind-history-loss" or "external-parent-bind-no-effect-lost-reply" or "external-parent-bind-partial-close") throw new IOException("Original Bind reply lost");
                if (Fault == "external-parent-bind-copy" && result is ExternalOriginalDomainBindObservation observation) return observation with { };
            }
            if (method.Name is nameof(IHybridCpuExternalRuntime.BindDomain) or nameof(IHybridCpuOriginalDomainBindRuntimeV1.BindOriginalDomain) && LoseBindReply) throw new IOException("original external parent Bind reply lost");
            return result;
        }
    }
    public class ReleaseSpy : DispatchProxy
    {
        public Action? AfterCreate, AfterCreateQuery;
        public ExternalChildDomainCreateReceipt? ExtraChildCreate;
        public ExternalGuestMemoryMapReceipt? ExtraGuestMap;
        public int BindCalls { get; private set; }
        public HybridCpuExternalRuntime Inner = null!;
        public string Fault = "exact";
        public Action? AfterRelease;
        public Action? AfterBindQuery;
        public Action? AfterParentClose, AfterParentQuery;
        public int ParentCloseCalls;
        public int ChildCloseCalls;
        public Action? AfterChildClose, AfterChildQuery;
        public Action? AfterGuestQuery;
        public Action? AfterGuestUnmap;
        public Action? AfterMap, AfterMapQuery;
        public Action<ExternalChildArtifactBindRequest>? BeforeBind;
        public Action? BeforeStart;
        public Exception? CallbackFailure;
        public int ReleaseCalls, UnmapCalls, StartCalls, MapCalls;
        public int OriginalStartCalls, OriginalStartQueries;
        public int DispositionQueries;
        public string? StartQueryFault;
        public int CreateCalls;
        public bool NativeHistoryLost;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IHybridCpuOriginalChildCreateRuntimeV1.CreateOriginalChild) && Fault.StartsWith("child-create-no-effect"))
            {
                var request = (ExternalOriginalChildCreateRequest)args![0]!;
                ExtraChildCreate = Inner.CreateChildDomain(request.Admission.Lease, request.CreationRequest with { RequestId = Guid.NewGuid() }).Receipt!;
                Assert.NotNull(ExtraChildCreate);
            }
            if (method!.Name == nameof(IHybridCpuOriginalGuestMapRuntimeV1.MapOriginalGuest) && Fault.StartsWith("guest-map-no-effect"))
            {
                var original = (ExternalOriginalGuestMapRequest)args![0]!;
                ExtraGuestMap = Inner.MapChildGuestMemory(original.Admission.Lease,
                    new(original.MappingRequest.ChildOffsetBytes, original.MappingRequest.LengthBytes, new(new(Guid.NewGuid()), new(800)))).Receipt!;
                Assert.NotNull(ExtraGuestMap);
            }
            if (method!.Name == nameof(IHybridCpuArtifactReleaseRuntimeV1.ResolveChildExecutableArtifactRelease) && Fault == "history-loss") return null;
            if (method.Name is nameof(IHybridCpuChildDomainRuntimeV3.UnmapChildGuestMemory) or nameof(IHybridCpuOriginalGuestUnmapRuntimeV1.UnmapOriginalGuest)) UnmapCalls++;
            if (method.Name == nameof(IHybridCpuOriginalChildExecutionStartRuntimeV1.StartOriginalChildExecution)) OriginalStartCalls++;
            if (method.Name == nameof(IHybridCpuOriginalChildExecutionStartRuntimeV1.ResolveOriginalChildExecutionStart)) OriginalStartQueries++;
            if (method.Name == nameof(IHybridCpuOriginalChildExecutionStartRuntimeV1.ResolveOriginalChildExecutionStart) && StartQueryFault == "loss") throw new IOException("Original no-effect query reply lost");
            if (method.Name is nameof(IHybridCpuChildDomainRuntimeV3.StartChildExecution) or nameof(IHybridCpuOriginalChildExecutionStartRuntimeV1.StartOriginalChildExecution)) { StartCalls++; BeforeStart?.Invoke(); }
            if (method.Name is nameof(IHybridCpuChildDomainRuntimeV3.MapChildGuestMemory) or nameof(IHybridCpuOriginalGuestMapRuntimeV1.MapOriginalGuest)) MapCalls++;
            if (method.Name is nameof(IHybridCpuChildDomainRuntimeV3.CreateChildDomain) or nameof(IHybridCpuOriginalChildCreateRuntimeV1.CreateOriginalChild)) CreateCalls++;
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.BindChildExecutableArtifact)) BeforeBind?.Invoke((ExternalChildArtifactBindRequest)args![1]!);
            if (method.Name == nameof(IHybridCpuOriginalGuestUnmapRuntimeV1.UnmapOriginalGuest) && Fault.StartsWith("guest-unmap-no-effect") && UnmapCalls == 1)
            {
                var nativeMappings = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("guestMappingRegistry", Private)!.GetValue(Inner)!;
                var nativeMapping = Assert.Single(nativeMappings.Values.Cast<object>());
                nativeMapping.GetType().GetProperty("Closed")!.SetValue(nativeMapping, true);
            }
            if (method.Name == nameof(IHybridCpuOriginalChildExecutionStartRuntimeV1.StartOriginalChildExecution) && Fault == "failed-start-refusal-history-duplicate")
                typeof(HybridCpuExternalRuntime).GetField("nextExecutionGeneration", Private)!.SetValue(Inner, long.MaxValue);
            var result = method.Invoke(Inner, args);
            if (method.Name == nameof(IHybridCpuOriginalChildCreateRuntimeV1.ResolveOriginalChildCreate))
            {
                if (Fault == "child-create-query-throw") throw new IOException("original child Create query interrupted");
                try { AfterCreateQuery?.Invoke(); } catch (Exception exception) { CallbackFailure = exception; throw; }
            }
            if (method.Name is nameof(IHybridCpuChildDomainRuntimeV3.CreateChildDomain) or nameof(IHybridCpuOriginalChildCreateRuntimeV1.CreateOriginalChild))
            {
                try { AfterCreate?.Invoke(); } catch (Exception exception) { CallbackFailure = exception; throw; }
                if (Fault == "child-create-history-duplicate")
                {
                    var roots = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(Inner)!;
                    var owner = Assert.Single(roots.Values.Cast<object>());
                    var history = (IList)owner.GetType().GetProperty("OriginalChildCreates")!.GetValue(owner)!;
                    history.Add(Assert.Single(history.Cast<object>()));
                    owner.GetType().GetProperty("OriginalChildCreatesIssued")!.SetValue(owner, 2UL);
                    throw new IOException("Original child Create reply lost after malformed inventory");
                }
                if (Fault == "child-create-history-loss") ClearNativeChildCreateHistory();
                if (Fault is "child-create-lost-reply" or "child-create-history-loss" or "child-create-independent-root" or "child-create-exhausted-root" or "child-create-no-effect-lost-reply" or "child-create-partial-close")
                    throw new IOException("original child Create reply lost");
                if (Fault == "child-create-copy" && result is ExternalOriginalChildCreateObservation observation) return observation with { };
            }
            if (method.Name == nameof(IHybridCpuOriginalGuestMapRuntimeV1.ResolveOriginalGuestMap))
            {
                if (Fault == "guest-map-query-throw") throw new IOException("original Map query interrupted");
                try { AfterMapQuery?.Invoke(); } catch (Exception exception) { CallbackFailure = exception; throw; }
            }
            if (method.Name is nameof(IHybridCpuChildDomainRuntimeV3.MapChildGuestMemory) or nameof(IHybridCpuOriginalGuestMapRuntimeV1.MapOriginalGuest))
            {
                try { AfterMap?.Invoke(); } catch (Exception exception) { CallbackFailure = exception; throw; }
                if (Fault == "guest-map-history-duplicate")
                {
                    var owners = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(Inner)!;
                    var owner = Assert.Single(owners.Values.Cast<object>());
                    var rows = (IList)owner.GetType().GetProperty("OriginalGuestMaps")!.GetValue(owner)!;
                    rows.Add(Assert.Single(rows.Cast<object>()));
                    owner.GetType().GetProperty("OriginalGuestMapsIssued")!.SetValue(owner, 2UL);
                    throw new IOException("Original Map reply lost after duplicate inventory");
                }
                if (Fault == "guest-map-history-loss") { ClearNativeGuestMapHistory(); throw new IOException("original Map history/reply lost"); }
                if (Fault is "guest-map-independent-child" or "guest-map-independent-parent") throw new IOException("original Map reply lost");
                if (Fault == "guest-map-copy" && result is ExternalOriginalGuestMapObservation observation) return observation with { };
            }
            if (method.Name is nameof(IHybridCpuChildDomainRuntimeV3.MapChildGuestMemory) or nameof(IHybridCpuOriginalGuestMapRuntimeV1.MapOriginalGuest) && Fault is "guest-map-lost-reply" or "guest-map-teardown-retry" or "guest-map-no-effect-lost-reply")
                throw new IOException("original native guest Map reply lost");
            if (method.Name == nameof(IHybridCpuOriginalGuestUnmapRuntimeV1.ResolveOriginalGuestUnmap))
                try { AfterGuestQuery?.Invoke(); } catch (Exception exception) { CallbackFailure = exception; throw; }
            if (method.Name is nameof(IHybridCpuChildDomainRuntimeV3.UnmapChildGuestMemory) or nameof(IHybridCpuOriginalGuestUnmapRuntimeV1.UnmapOriginalGuest))
            {
                try { AfterGuestUnmap?.Invoke(); } catch (Exception exception) { CallbackFailure = exception; throw; }
                if (Fault == "guest-unmap-history-loss") ClearNativeGuestUnmapHistory();
                if (Fault is "guest-unmap-lost-reply" or "guest-unmap-independent" or "guest-unmap-history-loss") throw new IOException("original native guest Unmap reply lost");
                if (Fault == "guest-unmap-no-effect-lost-reply" && UnmapCalls == 1) throw new IOException("original native no-effect reply lost");
                if (Fault == "guest-unmap-copy" && result is ExternalOriginalGuestUnmapObservation observation) return observation with { };
                if (Fault == "guest-unmap-malformed" && result is ExternalOriginalGuestUnmapObservation malformed) return malformed with { Raw = malformed.Raw with { Receipt = malformed.Raw.Receipt! with { IsTerminal = false } } };
            }
            if (method.Name is nameof(IHybridCpuChildDomainRuntimeV3.CloseChildDomain) or nameof(IHybridCpuOriginalChildCloseRuntimeV1.CloseOriginalChild))
            {
                ChildCloseCalls++;
                try { AfterChildClose?.Invoke(); } catch (Exception exception) { CallbackFailure = exception; throw; }
                if (Fault == "child-close-history-loss") { ClearNativeChildCloseHistory(); throw new IOException("native child Close history/reply lost"); }
                if (Fault is "child-close-lost-reply" or "child-close-independent" or "child-close-revision-exhaustion") throw new IOException("original native child Close reply lost");
                if (Fault == "child-close-copy" && result is ExternalOriginalChildCloseObservation observation) return observation with { };
            }
            if (method.Name == nameof(IHybridCpuOriginalChildCloseRuntimeV1.ResolveOriginalChildClose)) AfterChildQuery?.Invoke();
            if (method.Name == nameof(IHybridCpuOriginalDomainCloseRuntimeV1.CloseOriginalDomain))
            {
                ParentCloseCalls++;
                try { AfterParentClose?.Invoke(); } catch (Exception exception) { CallbackFailure = exception; throw; }
                if (Fault == "parent-history-loss")
                {
                    var parents = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(Inner)!;
                    var original = Assert.Single(parents.Values.Cast<object>());
                    original.GetType().GetProperty("OriginalClose")!.SetValue(original, null);
                    throw new IOException("original parent history and reply lost");
                }
                if (Fault == "parent-lost-reply") throw new IOException("original parent Close reply lost");
                if (Fault == "parent-copy" && result is ExternalOriginalDomainCloseObservation observation) return observation with { };
            }
            if (method.Name == nameof(IHybridCpuOriginalDomainCloseRuntimeV1.ResolveOriginalDomainClose)) AfterParentQuery?.Invoke();
            if (method.Name == nameof(IHybridCpuOriginalArtifactBindRuntimeV1.ResolveChildExecutableArtifactBind))
            {
                try { AfterBindQuery?.Invoke(); }
                catch (Exception exception) when (Fault == "bind-query-reentry") { CallbackFailure = exception; throw; }
            }
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.BindChildExecutableArtifact))
            {
                BindCalls++;
                if (Fault is "bind-parser-history-loss" or "bind-late-history-loss")
                {
                    var owner = Assert.Single(((IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(Inner)!).Values.Cast<object>());
                    ((IList)owner.GetType().GetProperty("OriginalArtifactBinds")!.GetValue(owner)!).Clear();
                }
                if (Fault is "bind-late-lost-reply" or "bind-late-history-loss" or "bind-parser-lost-reply" or "bind-parser-history-loss") throw new IOException("Actual Bind refusal reply lost");
                if (Fault is "bind-native-child-admission-copy" or "bind-native-mapping-admission-copy")
                {
                    var registry = (IDictionary)typeof(HybridCpuExternalRuntime).GetField(Fault == "bind-native-child-admission-copy" ? "childRegistry" : "guestMappingRegistry", Private)!.GetValue(Inner)!;
                    var owner = Assert.Single(registry.Values.Cast<object>());
                    var admission = owner.GetType().GetProperty("OriginalAdmission")!;
                    admission.SetValue(owner, typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(admission.GetValue(owner), null));
                    throw new IOException("Bind reply lost after admission copy");
                }
                if (Fault == "bind-malformed" && result is ExternalChildResult<ExternalChildArtifactBindReceipt> binding)
                    return binding with { Receipt = binding.Receipt! with { PackageSha256 = "foreign" } };
                if (Fault == "bind-native-history-duplicate")
                {
                    var registry = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(Inner)!;
                    var owner = Assert.Single(registry.Values.Cast<object>());
                    var history = (IList)owner.GetType().GetProperty("OriginalArtifactBinds")!.GetValue(owner)!;
                    history.Add(Assert.Single(history.Cast<object>()));
                    owner.GetType().GetProperty("ArtifactBindsIssued")!.SetValue(owner, 2UL);
                    throw new IOException("Bind reply lost after malformed native history");
                }
                if (Fault == "bind-history-loss")
                {
                    var registry = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(Inner)!;
                    var owner = Assert.Single(registry.Values.Cast<object>());
                    owner.GetType().GetProperty("ArtifactBind")!.SetValue(owner, null);
                    Assert.Null(owner.GetType().GetProperty("ArtifactBind")!.GetValue(owner));
                }
                if (Fault.StartsWith("bind-")) throw new IOException("Original Bind reply lost.");
            }
            if (method.Name is nameof(IHybridCpuChildDomainRuntimeV3.StartChildExecution) or nameof(IHybridCpuOriginalChildExecutionStartRuntimeV1.StartOriginalChildExecution) && Fault == "failed-start-lost-reply")
                throw new IOException("actual Start reply lost after invocation");
            if (method.Name == nameof(IHybridCpuOriginalChildExecutionStartRuntimeV1.ResolveOriginalChildExecutionStart) && StartQueryFault == "foreign" && result is ExternalOriginalChildExecutionStartObservation observedStart)
                return observedStart with { Request = observedStart.Request with { } };
            if (method.Name == nameof(IHybridCpuArtifactReleaseRuntimeV1.ResolveChildExecutableArtifactRelease) && Fault == "release-query-request-copy" && result is ExternalArtifactReleaseReceipt resolvedRelease)
                return resolvedRelease with { Request = resolvedRelease.Request with { } };
            if (method.Name == nameof(IHybridCpuOriginalArtifactBindDispositionRuntimeV1.ResolveOriginalArtifactBindDisposition))
            {
                DispositionQueries++;
                if (Fault == "bind-parser-query-loss") throw new IOException("Original disposition reply lost after pure lookup");
                if (result is ExternalOriginalArtifactBindDisposition proof)
                {
                    if (Fault == "bind-parser-query-generation") return proof with { ManifestGeneration = proof.ManifestGeneration + 1 };
                    if (Fault == "bind-parser-query-digest") return proof with { PackageSha256 = "foreign" };
                }
            }
            if (method.Name == nameof(IHybridCpuOriginalArtifactBindDispositionRuntimeV1.ResolveOriginalArtifactBindDisposition) && Fault is "bind-parser-copy-query" or "bind-late-copy-query" && result is ExternalOriginalArtifactBindDisposition disposition)
                return disposition with { Request = disposition.Request with { } };
            if (method.Name != nameof(IHybridCpuArtifactReleaseRuntimeV1.ReleaseChildExecutableArtifact)) return result;
            ReleaseCalls++; AfterRelease?.Invoke();
            if (Fault is "native-release-copy" or "native-release-bind-copy")
            {
                var nativeOwners = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(Inner)!;
                var nativeOwner = Assert.Single(nativeOwners.Values.Cast<object>());
                var factProperty = nativeOwner.GetType().GetProperty(Fault == "native-release-copy" ? "ArtifactRelease" : "ArtifactBind")!;
                var originalFact = factProperty.GetValue(nativeOwner)!;
                factProperty.SetValue(nativeOwner, typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(originalFact, null));
                Assert.Null(nativeOwner.GetType().GetProperty("Execution")!.GetValue(nativeOwner)); // Actual native join finished.
                throw new IOException("Original release reply lost after native fact copy");
            }
            if (Fault == "failed-start-release-history-loss")
            {
                var children = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(Inner)!;
                var nativeChild = Assert.Single(children.Values.Cast<object>());
                nativeChild.GetType().GetProperty("ArtifactRelease")!.SetValue(nativeChild, null);
                NativeHistoryLost = true;
            }
            if (Fault is "release-query-request-copy" or "lost-reply" or "history-loss" or "failed-start-release-lost-reply" or "failed-start-release-history-loss") throw new IOException("release reply lost after actual effect");
            if (Fault == "release-request-copy")
            {
                var raw = (ExternalChildResult<ExternalArtifactReleaseReceipt>)result!;
                return raw with { Receipt = raw.Receipt! with { Request = raw.Receipt.Request with { } } };
            }
            if (Fault == "malformed")
            {
                var raw = (ExternalChildResult<ExternalArtifactReleaseReceipt>)result!;
                return raw with { Receipt = raw.Receipt! with { Request = raw.Receipt.Request with { Operation = new(new(Guid.NewGuid()), new(1)) } } };
            }
            return result;
        }
        public void ClearNativeGuestMapHistory()
        {
            var children = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(Inner)!;
            var child = Assert.Single(children.Values.Cast<object>());
            ((IList)child.GetType().GetProperty("OriginalGuestMaps")!.GetValue(child)!).Clear();
        }
        public void ClearNativeChildCreateHistory()
        {
            var roots = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(Inner)!;
            var owner = Assert.Single(roots.Values.Cast<object>());
            ((IList)owner.GetType().GetProperty("OriginalChildCreates")!.GetValue(owner)!).Clear();
        }
        public void ClearNativeGuestUnmapHistory()
        {
            var mappings = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("guestMappingRegistry", Private)!.GetValue(Inner)!;
            Assert.Single(mappings.Values.Cast<object>()).GetType().GetProperty("OriginalUnmap")!.SetValue(Assert.Single(mappings.Values.Cast<object>()), null);
        }
        public void ClearNativeChildCloseHistory()
        {
            var children = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(Inner)!;
            var original = Assert.Single(children.Values.Cast<object>());
            original.GetType().GetProperty("OriginalClose")!.SetValue(original, null);
        }
    }
}



