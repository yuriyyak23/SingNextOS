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
    [InlineData("external-parent-bind-canonical-loss")] [InlineData("external-parent-bind-canonical-copy")]
    [InlineData("external-parent-bind-canonical-coordinated-copy")]
    [InlineData("external-parent-bind-native-raw-loss")] [InlineData("external-parent-bind-native-raw-copy")]
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
        HybridCpuExternalRuntime native = new HybridCpuExternalRuntime();
        IReleaseRuntime proxy = DispatchProxy.Create<IReleaseRuntime, ReleaseSpy>();
        ReleaseSpy spy = (ReleaseSpy)proxy; spy.Inner = native; spy.Fault = fault;
        IHybridCpuExternalRuntime externalRoot = fault == "external-parent-bind-prepared-incapable"
            ? DispatchProxy.Create<IOriginalBindOnlyRuntime, RootSpy>() : DispatchProxy.Create<IOriginalRootRuntime, RootSpy>();
        RootSpy rootSpy = (RootSpy)externalRoot; rootSpy.Inner = native;
        rootSpy.LoseBindReply = fault == "external-parent-bind-lost-reply";
        rootSpy.Fault = fault;
        ConstructorInfo constructor = typeof(HybridCpuExecutableChildAdapter).GetConstructor(Private, null,
            [typeof(IHybridCpuExternalRuntime), typeof(IHybridCpuChildDomainRuntimeV3), typeof(ISemanticTraceSinkV1)], null)!;
        HybridCpuExecutableChildAdapter adapter = (HybridCpuExecutableChildAdapter)constructor.Invoke([externalRoot, proxy, null]);
        NeutralDomainRuntimeFacade model = new NeutralDomainRuntimeFacade();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(model, adapter);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 777, 1119);
        ProcessHandle admin = TestFixtures.Create(kernel, 99, 999).Handle;
        CapabilityId cap = kernel.MintCapability(new DomainId(999), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(admin, cap, owner, "child-create-reclaim",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 8192)]).Value!.ProcessBudget;
        OwnedBuffer<byte> region = kernel.AllocateBuffer<byte>(owner, 8192).Value!;
        PlatformDomainBinding root = kernel.BindPlatformDomain(owner).Value!;
        const PlatformChildAuthorityClass authority = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory;
        PlatformAuthorityBridge bridge = kernel.PlatformAuthority;
        IDictionary? routes = null; object? key = null, original = null;
        spy.AfterCreate = () =>
        {
            if (!fault.EndsWith("owner")) return;
            object target = fault.EndsWith("adapter-owner") ? (object)adapter : fault.EndsWith("provider-owner") ? provider : bridge;
            string field = fault.EndsWith("adapter-owner") ? "parents" : "_domains";
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
        object? bindCustodyOwner = null;
        PropertyInfo? bindCustodySlot = null;
        ExternalOriginalDomainBindObservation? bindCustodyOriginal = null;
        PropertyInfo? nativeBindSlot = null;
        ExternalOriginalDomainBindObservation? nativeBindOriginal = null;
        if (fault.StartsWith("external-parent-bind-native-raw-", StringComparison.Ordinal))
        {
            rootSpy.LoseBindReply = true;
            rootSpy.AfterBind = () =>
            {
                Assert.False(Monitor.IsEntered(adapter.GetType().GetField("sync", Private)!.GetValue(adapter)!));
                nativeBindSlot = rootSpy.Prepared!.GetType().GetProperty("Observation")!;
                nativeBindOriginal = (ExternalOriginalDomainBindObservation)nativeBindSlot.GetValue(rootSpy.Prepared)!;
                Assert.NotNull(nativeBindOriginal);
                nativeBindSlot.SetValue(rootSpy.Prepared, fault.EndsWith("loss", StringComparison.Ordinal) ? null : nativeBindOriginal with { });
            };
        }
        rootSpy.AfterBindQuery = () =>
        {
            if (fault.StartsWith("external-parent-bind-canonical-", StringComparison.Ordinal))
                rootSpy.BeforeQuery = () =>
                {
                    if (bindCustodyOriginal is not null) return;
                    Assert.False(Monitor.IsEntered(adapter.GetType().GetField("sync", Private)!.GetValue(adapter)!));
                    bindCustodyOwner = Assert.Single(((IDictionary)adapter.GetType().GetField("parents", Private)!.GetValue(adapter)!).Values.Cast<object>());
                    bindCustodySlot = bindCustodyOwner.GetType().GetProperty("NativeBindValidated")!;
                    bindCustodyOriginal = (ExternalOriginalDomainBindObservation)bindCustodySlot.GetValue(bindCustodyOwner)!;
                    Assert.NotNull(bindCustodyOriginal);
                    bindCustodySlot.SetValue(bindCustodyOwner, fault.EndsWith("loss", StringComparison.Ordinal) ? null : bindCustodyOriginal with { });
                    if (fault.EndsWith("coordinated-copy", StringComparison.Ordinal))
                        bindCustodyOwner.GetType().GetProperty("BindObservation")!.SetValue(bindCustodyOwner, bindCustodyOriginal.Raw with { });
                };
            if (fault is "external-parent-bind-source" or "external-parent-bind-known-history-loss")
                rootSpy.BeforeQuery = () => { rootSpy.ThrowSource = true; if (fault == "external-parent-bind-known-history-loss") rootSpy.ClearBindHistory(); };
            if (fault == "external-parent-bind-reentry")
            {
                Assert.False(Monitor.IsEntered(adapter.GetType().GetField("sync", Private)!.GetValue(adapter)!));
                Assert.False(bridge.CreateChildDomain(root, root.Subject, new(new(1, 8192), new(authority, authority))).IsSuccess);
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            }
        };
        KernelResult<PlatformChildBinding> admission = bridge.CreateChildDomain(root, root.Subject, new(new(1, 8192), new(authority, authority)));
        Assert.Equal(fault is "child-create-reentry" or "external-parent-bind-reentry", admission.IsSuccess);
        int expectedCreates = fault.StartsWith("external-parent-bind-") && fault is not ("external-parent-bind-reentry" or "external-parent-bind-provider-owner" or "external-parent-bind-bridge-owner") ? 0 : 1;
        Assert.Equal(expectedCreates, spy.CreateCalls);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        spy.AfterCreate = null; spy.AfterCreateQuery = null; rootSpy.BeforeQuery = null; rootSpy.ThrowSource = false; rootSpy.LoseBindReply = false;
        rootSpy.AfterBind = null; rootSpy.AfterBindQuery = null;
        if (nativeBindOriginal is not null)
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1, rootSpy.OriginalBindCalls);
            Assert.Equal(0, spy.CreateCalls);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            nativeBindSlot!.SetValue(rootSpy.Prepared, nativeBindOriginal);
        }
        if (bindCustodyOriginal is not null)
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(0, spy.CreateCalls);
            Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            bindCustodySlot!.SetValue(bindCustodyOwner, bindCustodyOriginal);
            bindCustodyOwner!.GetType().GetProperty("BindObservation")!.SetValue(bindCustodyOwner, bindCustodyOriginal.Raw);
        }
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
        IDictionary roots = (IDictionary)bridge.GetType().GetField("_domains", Private)!.GetValue(bridge)!;
        object rootOwner = roots[root.BindingId]!;
        if (fault == "child-create-independent-root")
        { PropertyInfo state = rootOwner.GetType().GetProperty("AuthorityState")!; state.SetValue(rootOwner, state.GetValue(rootOwner)); }
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
        if (fault == "child-create-known-history-loss")
        {
            // A consumed SDK fact cannot reconstruct the independently lost native history.
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(expectedCreates, spy.CreateCalls);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.NotEqual("Closed", Assert.Single(((IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(native)!).Values.Cast<object>()).GetType().GetProperty("Lifecycle")!.GetValue(Assert.Single(((IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(native)!).Values.Cast<object>()))!.ToString());
            return;
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
        HybridCpuExternalRuntime native = new HybridCpuExternalRuntime();
        IReleaseRuntime proxy = DispatchProxy.Create<IReleaseRuntime, ReleaseSpy>();
        ReleaseSpy spy = (ReleaseSpy)proxy; spy.Inner = native; spy.Fault = fault;
        IHybridCpuExternalRuntime root = DispatchProxy.Create<IHybridCpuExternalRuntime, RootSpy>();
        RootSpy rootSpy = (RootSpy)root; rootSpy.Inner = native;
        ConstructorInfo constructor = typeof(HybridCpuExecutableChildAdapter).GetConstructor(Private, null,
            [typeof(IHybridCpuExternalRuntime), typeof(IHybridCpuChildDomainRuntimeV3), typeof(ISemanticTraceSinkV1)], null)!;
        HybridCpuExecutableChildAdapter adapter = (HybridCpuExecutableChildAdapter)constructor.Invoke([root, proxy, null]);
        NeutralDomainRuntimeFacade model = new NeutralDomainRuntimeFacade();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(model, adapter);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 777, 1119);
        ProcessHandle admin = TestFixtures.Create(kernel, 99, 999).Handle;
        CapabilityId budgetCap = kernel.MintCapability(new DomainId(999), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "guest-map-reclaim",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 8192), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 8192)]).Value!.ProcessBudget;
        OwnedBuffer<byte> region = kernel.AllocateBuffer<byte>(owner, 8192).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId cap = kernel.MintCapability(binding.Subject.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        PlatformOwnedRegionSliceMapping mapping = kernel.MapPlatformOwnedRegionSlice(owner, binding, cap, region.Handle, 0, 8192, PlatformMemoryAccess.Read).Value!;
        const PlatformChildAuthorityClass authority = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory;
        PlatformAuthorityBridge bridge = kernel.PlatformAuthority;
        PlatformChildBinding child = bridge.CreateChildDomain(binding, binding.Subject, new(new(1, 8192), new(authority, authority))).Value!;
        IDictionary? ownerRoutes = null; object? ownerKey = null, ownerRecord = null;
        if (fault.EndsWith("owner"))
        {
            object target = fault.EndsWith("adapter-owner") ? (object)adapter : fault.EndsWith("provider-owner") ? provider : bridge;
            string field = fault.EndsWith("adapter-owner") ? "children" : fault.EndsWith("provider-owner") ? "_providerChildren" : "_childBindings";
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
        KernelResult<PlatformGuestMapping> admission = bridge.MapChildGuestRegion(child, mapping.Mapping, new(0, 8192), PlatformGuestMemoryAccess.Read);
        Assert.Equal(fault == "guest-map-reentry", admission.IsSuccess);
        IDictionary nativeMappings = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("guestMappingRegistry", Private)!.GetValue(native)!;
        Assert.Single(nativeMappings.Values.Cast<object>());
        Assert.Equal(1, spy.MapCalls);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        spy.AfterMap = null; spy.AfterMapQuery = null; rootSpy.BeforeQuery = null; rootSpy.ThrowSource = false;
        if (ownerRoutes is not null) ownerRoutes[ownerKey!] = ownerRecord;
        if (fault == "guest-map-query-throw") spy.Fault = "exact";
        if (fault.StartsWith("guest-map-independent-"))
        {
            string target = fault.EndsWith("child") ? "_originalChildOwners" : "_mappings";
            IDictionary records = (IDictionary)bridge.GetType().GetField(target, Private)!.GetValue(bridge)!;
            object record = Assert.Single(records.Values.Cast<object>());
            PropertyInfo property = record.GetType().GetProperty(fault.EndsWith("child") ? "State" : "ClosureState")!;
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
    [InlineData("release-custody-loss")] [InlineData("release-custody-copy")]
    [InlineData("provider-release-loss")] [InlineData("provider-release-substitution")]
    [InlineData("bridge-release-loss")] [InlineData("bridge-release-substitution")]
    [InlineData("parent-lost-reply")] [InlineData("parent-copy")]
    [InlineData("parent-history-loss")] [InlineData("parent-source")]
    [InlineData("parent-owner")] [InlineData("parent-reentry")]
    [InlineData("parent-known-history-loss")] [InlineData("parent-no-effect")]
    [InlineData("parent-no-effect-copy")]
    [InlineData("parent-canonical-loss")] [InlineData("parent-canonical-copy")]
    [InlineData("parent-native-raw-loss")] [InlineData("parent-native-raw-copy")]
    [InlineData("parent-child-closure-loss")] [InlineData("parent-child-closure-copy")]
    [InlineData("parent-pre-source")] [InlineData("parent-query-throw")]
    [InlineData("child-close-lost-reply")]
    [InlineData("child-close-copy")] [InlineData("child-close-history-loss")]
    [InlineData("child-close-source")] [InlineData("child-close-owner")]
    [InlineData("child-close-query-throw")] [InlineData("child-close-reentry")]
    [InlineData("child-close-canonical-loss")] [InlineData("child-close-canonical-copy")]
    [InlineData("child-close-bridge-loss")] [InlineData("child-close-bridge-copy")]
    [InlineData("child-close-independent")] [InlineData("child-close-revision-exhaustion")]
    [InlineData("child-close-known-history-loss")]
    [InlineData("child-close-no-effect")]
    [InlineData("child-close-no-effect-copy")]
    [InlineData("guest-unmap-lost-reply")]
    [InlineData("guest-unmap-copy")] [InlineData("guest-unmap-history-loss")]
    [InlineData("guest-unmap-source")] [InlineData("guest-unmap-known-history-loss")]
    [InlineData("guest-unmap-canonical-loss")] [InlineData("guest-unmap-canonical-copy")]
    [InlineData("guest-unmap-bridge-custody-loss")] [InlineData("guest-unmap-bridge-custody-copy")]
    [InlineData("guest-proof-loss")] [InlineData("guest-proof-copy")]
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
        HybridCpuExternalRuntime native = new HybridCpuExternalRuntime();
        IReleaseRuntime proxy = DispatchProxy.Create<IReleaseRuntime, ReleaseSpy>();
        ReleaseSpy spy = (ReleaseSpy)proxy; spy.Inner = native;
        IHybridCpuExternalRuntime root = DispatchProxy.Create<IHybridCpuExternalRuntime, RootSpy>();
        RootSpy rootSpy = (RootSpy)root; rootSpy.Inner = native;
        ConstructorInfo constructor = typeof(HybridCpuExecutableChildAdapter).GetConstructor(Private, null,
            [typeof(IHybridCpuExternalRuntime), typeof(IHybridCpuChildDomainRuntimeV3), typeof(ISemanticTraceSinkV1)], null)!;
        HybridCpuExecutableChildAdapter adapter = (HybridCpuExecutableChildAdapter)constructor.Invoke([root, proxy, null]);
        NeutralDomainRuntimeFacade model = new NeutralDomainRuntimeFacade();
        NativeTransferRawCustodyConsumerTests.ISourceRuntime releaseSource =DispatchProxy.Create<NativeTransferRawCustodyConsumerTests.ISourceRuntime,NativeTransferRawCustodyConsumerTests.SourceSpy>();
        NativeTransferRawCustodyConsumerTests.SourceSpy releaseSourceSpy =(NativeTransferRawCustodyConsumerTests.SourceSpy)(object)releaseSource;releaseSourceSpy.Inner=model;
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(fault.StartsWith("provider-release-",StringComparison.Ordinal)?releaseSource:model, adapter);
        IReleaseBridgeProvider releaseBridgeProvider =DispatchProxy.Create<IReleaseBridgeProvider,ExactVirtualIoBackingConsumerTests.BridgeBindSpy>();
        ExactVirtualIoBackingConsumerTests.BridgeBindSpy releaseBridgeSpy =(ExactVirtualIoBackingConsumerTests.BridgeBindSpy)(object)releaseBridgeProvider;releaseBridgeSpy.Inner=provider;
        RuntimeKernel kernel = new RuntimeKernel(fault.StartsWith("bridge-release-",StringComparison.Ordinal) || fault.StartsWith("child-close-bridge-",StringComparison.Ordinal) || fault.StartsWith("guest-unmap-bridge-custody-",StringComparison.Ordinal)?releaseBridgeProvider:provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 776, 1118);
        ProcessHandle admin = TestFixtures.Create(kernel, 98, 998).Handle;
        CapabilityId budgetCap = kernel.MintCapability(new DomainId(998), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "artifact-release",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 8192), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 8192)]).Value!.ProcessBudget;
        OwnedBuffer<byte> region = kernel.AllocateBuffer<byte>(owner, 8192).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        CapabilityId cap = kernel.MintCapability(binding.Subject.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        PlatformOwnedRegionSliceMapping mapping = kernel.MapPlatformOwnedRegionSlice(owner, binding, cap, region.Handle, 0, 8192, PlatformMemoryAccess.Read).Value!;
        PlatformAuthorityBridge bridge = kernel.PlatformAuthority;
        const PlatformChildAuthorityClass authority = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.Execution | PlatformChildAuthorityClass.GuestMemory;
        // Trusted bridge fixture: public RuntimeAdmission gate remains closed for Executable contour.
        PlatformChildBinding child = bridge.CreateChildDomain(binding, binding.Subject, new(new(1, 8192), new(authority, authority))).Value!;
        PlatformGuestMapping guest = bridge.MapChildGuestRegion(child, mapping.Mapping, new(0, 8192), PlatformGuestMemoryAccess.Read | PlatformGuestMemoryAccess.Execute).Value!;
        spy.Fault = fault;
        bool observedOriginalBindIntent = false;
        spy.BeforeBind = externalRequest =>
        {
            object adapterOwner = Assert.Single(((IDictionary)adapter.GetType().GetField("originalMappings", Private)!.GetValue(adapter)!).Values.Cast<object>());
            object adapterIntent = adapterOwner.GetType().GetProperty("ArtifactBind")!.GetValue(adapterOwner)!;
            Assert.Same(externalRequest, adapterIntent.GetType().GetProperty("External")!.GetValue(adapterIntent));
            object providerOwner = Assert.Single(((IDictionary)provider.GetType().GetField("_originalProviderGuestMappings", Private)!.GetValue(provider)!).Values.Cast<object>());
            object providerIntent = providerOwner.GetType().GetProperty("OriginalArtifactBindInvocation")!.GetValue(providerOwner)!;
            Assert.NotNull(providerIntent);
            Assert.Null(providerIntent.GetType().GetProperty("Observation")!.GetValue(providerIntent));
            object localOwner = Assert.Single(((IDictionary)bridge.GetType().GetField("_originalExecutableArtifactOwners", Private)!.GetValue(bridge)!).Values.Cast<object>());
            object localIntent = localOwner.GetType().GetProperty("BindInvocation")!.GetValue(localOwner)!;
            Assert.True((bool)localIntent.GetType().GetProperty("Invoked")!.GetValue(localIntent)!);
            observedOriginalBindIntent = true;
        };
        bool parserRefusal = fault.StartsWith("bind-parser-");
        byte[] bindPackage = parserRefusal ? new byte[] { 1, 2, 3, 4 } : ExecutableAdapterStartAdmissionTests.CreateExecutablePackage();
        if (fault.StartsWith("bind-late-"))
        {
            ulong imageBase = global::System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bindPackage.AsSpan(32));
            ulong entry = global::System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bindPackage.AsSpan(40));
            global::System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bindPackage.AsSpan(32), 0x4000_0000);
            global::System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bindPackage.AsSpan(40), 0x4000_0000 + entry - imageBase);
            bindPackage.AsSpan(288, 32).Clear();
            global::System.Security.Cryptography.SHA256.HashData(bindPackage).CopyTo(bindPackage, 288);
        }
        KernelResult<PlatformExecutableArtifactBinding> bound = bridge.BindChildExecutableArtifact(child, guest, bindPackage, 3);
        Assert.True(observedOriginalBindIntent);
        Assert.Equal(!fault.StartsWith("bind-"), bound.IsSuccess); PlatformExecutableArtifactBinding artifact = bound.Value;
        object target = fault.EndsWith("adapter-owner") ? adapter : fault.EndsWith("provider-owner") ? provider : bridge;
        string field = fault.EndsWith("adapter-owner") ? "artifacts" : fault.EndsWith("provider-owner") ? "_providerArtifacts" : "_executableArtifacts";
        IDictionary routes = (IDictionary)target.GetType().GetField(field, Private)!.GetValue(target)!;
        object key = Assert.Single(routes.Keys.Cast<object>()); object original = routes[key]!;
        if (fault.StartsWith("bind-"))
        {
            artifact = (PlatformExecutableArtifactBinding)original.GetType().GetProperty("Binding")!.GetValue(original)!;
            Assert.False(bridge.BindChildExecutableArtifact(child, guest, ExecutableAdapterStartAdmissionTests.CreateExecutablePackage(), 3).IsSuccess);
            Assert.Equal(1, spy.BindCalls);
        }
        object gate = target.GetType().GetField(target == adapter ? "sync" : target == provider ? "_childCreateCommitGate" : "_secureDomainLifecycleGate", Private)!.GetValue(target)!;
        bool preRefusal = fault.StartsWith("pre-") || fault.StartsWith("absent-");
        if (preRefusal)
        {
            if (fault.StartsWith("absent-")) routes.Remove(key);
            else routes[key] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(original, null)!;
            Assert.False(bridge.UnmapChildGuestRegion(guest).IsSuccess);
            Assert.Equal(0, spy.ReleaseCalls); Assert.Equal(0, spy.UnmapCalls);
            routes[key] = original;
        }
        object? releaseCustodyOwner=null;PropertyInfo? releaseCustodyRoute=null;ExternalChildResult<ExternalArtifactReleaseReceipt>? releaseCustodyRaw=null;
        object? bridgeReleaseOwner=null;PropertyInfo? bridgeReleaseRoute=null;PlatformAuthorityResult<PlatformArtifactReleaseReceipt>? bridgeReleaseRaw=null;
        if(fault.StartsWith("bridge-release-",StringComparison.Ordinal))releaseBridgeSpy.BeforeSource=()=>
        {
            if(bridgeReleaseRaw is not null)return;
            object[] candidates =((IDictionary)bridge.GetType().GetField("_originalExecutableArtifactOwners",Private)!.GetValue(bridge)!).Values.Cast<object>().ToArray();
            if(candidates.Length!=1 || candidates[0].GetType().GetProperty("ReleaseObservation")!.GetValue(candidates[0]) is not PlatformAuthorityResult<PlatformArtifactReleaseReceipt> {IsSuccess:true} producedBridgeRelease)return;
            bridgeReleaseOwner=candidates[0];bridgeReleaseRoute=bridgeReleaseOwner.GetType().GetProperty("ReleaseObservation")!;bridgeReleaseRaw=producedBridgeRelease;
            Assert.False(Monitor.IsEntered(bridge.GetType().GetField("_secureDomainLifecycleGate",Private)!.GetValue(bridge)!));
            bridgeReleaseRoute.SetValue(bridgeReleaseOwner,fault.EndsWith("loss",StringComparison.Ordinal)?null:producedBridgeRelease with {Value=producedBridgeRelease.Value with {NoFurtherUse=false}});
        };
        object? providerReleaseOwner=null;PropertyInfo? providerReleaseRoute=null;NeutralVirtualizationResult<NeutralArtifactReleaseReceipt>? providerReleaseRaw=null;
        if(fault.StartsWith("provider-release-",StringComparison.Ordinal))releaseSourceSpy.BeforeSource=()=>
        {
            if(providerReleaseRaw is not null)return;
            object[] candidates =((IDictionary)provider.GetType().GetField("_originalProviderArtifacts",Private)!.GetValue(provider)!).Values.Cast<object>().ToArray();
            if(candidates.Length!=1 || candidates[0].GetType().GetProperty("ReleaseObservation")!.GetValue(candidates[0]) is not NeutralVirtualizationResult<NeutralArtifactReleaseReceipt> {IsSuccess:true} producedRelease)return;
            providerReleaseOwner=candidates[0];providerReleaseRoute=providerReleaseOwner.GetType().GetProperty("ReleaseObservation")!;providerReleaseRaw=producedRelease;
            Assert.False(Monitor.IsEntered(provider.GetType().GetField("_childCreateCommitGate",Private)!.GetValue(provider)!));
            providerReleaseRoute.SetValue(providerReleaseOwner,fault.EndsWith("loss",StringComparison.Ordinal)?null:producedRelease with {Value=producedRelease.Value with {NoFurtherUse=false}});
        };
        OwnedBuffer<byte>? releaseForeign =fault.StartsWith("release-custody-",StringComparison.Ordinal) || fault.StartsWith("provider-release-",StringComparison.Ordinal) || fault.StartsWith("bridge-release-",StringComparison.Ordinal)?kernel.AllocateBuffer<byte>(admin,16).Value:null;
        if(releaseForeign is not null)releaseForeign.Span[0]=0x5E;
        spy.AfterRelease = () =>
        {
            if(fault.StartsWith("release-custody-",StringComparison.Ordinal))rootSpy.BeforeQuery=()=>
            {
                if(releaseCustodyRaw is not null)return;
                releaseCustodyOwner=Assert.Single(((IDictionary)adapter.GetType().GetField("originalArtifacts",Private)!.GetValue(adapter)!).Values.Cast<object>());
                releaseCustodyRoute=releaseCustodyOwner.GetType().GetProperty("ReleaseObservation")!;
                releaseCustodyRaw=(ExternalChildResult<ExternalArtifactReleaseReceipt>)releaseCustodyRoute.GetValue(releaseCustodyOwner)!;
                Assert.False(Monitor.IsEntered(adapter.GetType().GetField("sync",Private)!.GetValue(adapter)!));
                releaseCustodyRoute.SetValue(releaseCustodyOwner,fault.EndsWith("loss",StringComparison.Ordinal)?null:releaseCustodyRaw with {Receipt=releaseCustodyRaw.Receipt! with {}});
            };
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
            string dependencyField = fault.EndsWith("child") ? "_childBindings" : fault.EndsWith("guest") ? "_guestBindings" : "_mappings";
            object dependencyKey = dependencyField == "_childBindings" ? child.BindingId : dependencyField == "_guestBindings" ? guest.MappingId : mapping.Mapping.MappingId;
            IDictionary dependencies = (IDictionary)bridge.GetType().GetField(dependencyField, Private)!.GetValue(bridge)!;
            object dependency = dependencies[dependencyKey]!;
            PropertyInfo state = dependency.GetType().GetProperty(dependencyField == "_childBindings" ? "State" : dependencyField == "_guestBindings" ? "Closure" : "ClosureState")!;
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
                    object nativeFact = Assert.Single(((IDictionary)adapter.GetType().GetField("originalMappings", Private)!.GetValue(adapter)!).Values.Cast<object>());
                    object retained = nativeFact.GetType().GetProperty("ArtifactBind")!.GetValue(nativeFact)!;
                    ExternalOriginalArtifactBindDisposition disposition = (ExternalOriginalArtifactBindDisposition)retained.GetType().GetProperty("Disposition")!.GetValue(retained)!;
                    Assert.Equal(!fault.StartsWith("bind-late-"), disposition.NoEffect);
                    if (fault.StartsWith("bind-late-")) Assert.Same(disposition.CleanupArtifact, retained.GetType().GetProperty("ResolutionObservation")!.GetValue(retained));
                    foreach ((object? component, string? lockName) in new[] { ((object)bridge, "_secureDomainLifecycleGate"), ((object)provider, "_childCreateCommitGate"), ((object)adapter, "sync") })
                        Assert.False(Monitor.IsEntered(component.GetType().GetField(lockName, Private)!.GetValue(component)!));
                    if (fault is "bind-parser-source-aba" or "bind-late-source-aba") rootSpy.FeatureGenerationOverride = native.QueryFeatures().Generation + 1;
                    if (fault is "bind-parser-reentry" or "bind-late-reentry") Assert.False(bridge.UnmapChildGuestRegion(guest).IsSuccess);
                    if (fault == "bind-parser-partial-consumption")
                    {
                        object guestOwner = ((IDictionary)bridge.GetType().GetField("_originalGuestOwners", Private)!.GetValue(bridge)!)[guest.MappingId]!;
                        guestOwner.GetType().GetProperty("PendingSecureRegionEffects")!.SetValue(guestOwner, 1);
                    }
                };
            foreach ((object? owner, string? fieldName) in new[] { ((object)bridge, "_secureDomainLifecycleGate"), ((object)provider, "_childCreateCommitGate"), ((object)adapter, "sync") })
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
            IDictionary adapterRoutes = (IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("artifacts", Private)!.GetValue(adapter)!;
            object adapterKey = Assert.Single(adapterRoutes.Keys.Cast<object>()); object adapterOriginal = adapterRoutes[adapterKey]!;
            NeutralArtifactReleaseRequest nativeRequest = (NeutralArtifactReleaseRequest)adapterOriginal.GetType().GetProperty("ReleaseRequest")!.GetValue(adapterOriginal)!;
            adapterRoutes[adapterKey] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(adapterOriginal, null)!;
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.ResolveExecutableArtifactRelease(nativeRequest).Status);
            adapterRoutes[adapterKey] = adapterOriginal;
            IDictionary providerRoutes = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_providerArtifacts", Private)!.GetValue(provider)!;
            object providerKey = Assert.Single(providerRoutes.Keys.Cast<object>()); object providerOriginal = providerRoutes[providerKey]!;
            PlatformArtifactReleaseRequest platformRequest = (PlatformArtifactReleaseRequest)providerOriginal.GetType().GetProperty("ReleaseRequest")!.GetValue(providerOriginal)!;
            providerRoutes[providerKey] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(providerOriginal, null)!;
            Assert.Equal(PlatformAuthorityStatus.Faulted, provider.ResolveExecutableArtifactRelease(platformRequest).Status);
            providerRoutes[providerKey] = providerOriginal;
            Assert.Equal(0, spy.ReleaseCalls);
        };
        IDictionary? unmapRoutes = null; object? unmapKey = null, unmapOriginal = null;
        if (fault.StartsWith("guest-unmap-") && fault.EndsWith("owner"))
        {
            object unmapTarget = fault.EndsWith("adapter-owner") ? (object)adapter : fault.EndsWith("provider-owner") ? provider : bridge;
            string unmapField = fault.EndsWith("adapter-owner") ? "mappings" : fault.EndsWith("provider-owner") ? "_providerGuestMappings" : "_guestBindings";
            unmapRoutes = (IDictionary)unmapTarget.GetType().GetField(unmapField, Private)!.GetValue(unmapTarget)!;
            unmapKey = Assert.Single(unmapRoutes.Keys.Cast<object>()); unmapOriginal = unmapRoutes[unmapKey]!;
            spy.AfterGuestUnmap = () => unmapRoutes[unmapKey] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(unmapOriginal, null)!;
        }
        object? unmapCustodyFact=null;FieldInfo? unmapCustodyRoute=null;ExternalOriginalGuestUnmapObservation? unmapCustodyRaw=null;
        object? guestBridgeOwner=null,guestBridgeOriginal=null;PropertyInfo? guestBridgeRoute=null;
        if(fault.StartsWith("guest-unmap-bridge-custody-",StringComparison.Ordinal))releaseBridgeSpy.BeforeSource=()=>
        {
            if(guestBridgeOriginal is not null)return;
            object[] guestCandidates =((IDictionary)bridge.GetType().GetField("_originalGuestOwners",Private)!.GetValue(bridge)!).Values.Cast<object>().ToArray();
            if(guestCandidates.Length!=1 || guestCandidates[0].GetType().GetProperty("LastClosureReceipt")!.GetValue(guestCandidates[0]) is not { } originalGuestClosure)return;
            guestBridgeOwner=guestCandidates[0];guestBridgeRoute=guestBridgeOwner.GetType().GetProperty("LastClosureReceipt")!;guestBridgeOriginal=originalGuestClosure;
            Assert.False(Monitor.IsEntered(bridge.GetType().GetField("_secureDomainLifecycleGate",Private)!.GetValue(bridge)!));
            guestBridgeRoute.SetValue(guestBridgeOwner,fault.EndsWith("loss",StringComparison.Ordinal)?null:typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(originalGuestClosure,null));
        };
        OwnedBuffer<byte>? unmapForeign =fault.StartsWith("guest-unmap-canonical-",StringComparison.Ordinal) || fault.StartsWith("guest-proof-",StringComparison.Ordinal) || fault.StartsWith("guest-unmap-bridge-custody-",StringComparison.Ordinal) ? kernel.AllocateBuffer<byte>(admin,16).Value : null;
        if(unmapForeign is not null)unmapForeign.Span[0]=0x6C;
        spy.AfterGuestQuery = () =>
        {
            if(fault.StartsWith("guest-unmap-canonical-",StringComparison.Ordinal))rootSpy.BeforeQuery=()=>
            {
                if(unmapCustodyRaw is not null)return;
                object unmapMapping =Assert.Single(((IDictionary)adapter.GetType().GetField("originalMappings",Private)!.GetValue(adapter)!).Values.Cast<object>());
                unmapCustodyFact=unmapMapping.GetType().GetProperty("OriginalUnmap")!.GetValue(unmapMapping)!;
                unmapCustodyRoute=unmapCustodyFact.GetType().GetField("Validated")!;
                unmapCustodyRaw=(ExternalOriginalGuestUnmapObservation)unmapCustodyRoute.GetValue(unmapCustodyFact)!;Assert.NotNull(unmapCustodyRaw);
                Assert.False(Monitor.IsEntered(adapter.GetType().GetField("sync",Private)!.GetValue(adapter)!));
                unmapCustodyRoute.SetValue(unmapCustodyFact,fault.EndsWith("loss",StringComparison.Ordinal)?null:unmapCustodyRaw with {});
            };
            if (fault == "guest-unmap-independent-draining")
            {
                IDictionary guestOwners = (IDictionary)bridge.GetType().GetField("_originalGuestOwners", Private)!.GetValue(bridge)!;
                object originalGuest = Assert.Single(guestOwners.Values.Cast<object>());
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
            IDictionary guestOwners = (IDictionary)bridge.GetType().GetField("_originalGuestOwners", Private)!.GetValue(bridge)!;
            Assert.Single(guestOwners.Values.Cast<object>()).GetType().GetField("_lifecycleRevision", Private)!.SetValue(Assert.Single(guestOwners.Values.Cast<object>()), long.MaxValue);
        }
        KernelResult first = bridge.UnmapChildGuestRegion(guest);
        if(releaseForeign is not null)
        {
            Assert.Equal(0x5E,releaseForeign.Span[0]);Assert.Equal(0,spy.UnmapCalls);
            object releasedNativeChild =Assert.Single(((IDictionary)native.GetType().GetField("childRegistry",Private)!.GetValue(native)!).Values.Cast<object>());
            Assert.Null(releasedNativeChild.GetType().GetProperty("Execution")!.GetValue(releasedNativeChild));
            object releasedNativeFact =releasedNativeChild.GetType().GetProperty("ArtifactRelease")!.GetValue(releasedNativeChild)!;
            if(releaseCustodyRaw is not null)Assert.Same(releaseCustodyRaw.Receipt,releasedNativeFact.GetType().GetProperty("OriginalObservation")!.GetValue(releasedNativeFact));
            else Assert.NotNull(releasedNativeFact.GetType().GetProperty("OriginalObservation")!.GetValue(releasedNativeFact));
        }
        if(unmapForeign is not null)Assert.Equal(0x6C,unmapForeign.Span[0]);
        Assert.Null(spy.CallbackFailure);
        if(fault=="history-loss")
        {
            IDictionary sources =(IDictionary)provider.GetType().GetField("_originalProviderChildren",Private)!.GetValue(provider)!;
            object source =Assert.Single(sources.Values.Cast<object>());
            PlatformProviderChildDomainLease lease =(PlatformProviderChildDomainLease)source.GetType().GetProperty("Lease")!.GetValue(source)!;
            int calls =spy.TransitionCalls;
            Assert.False(provider.TransitionChildDomain(lease,PlatformChildDomainTransition.BeginDrain).IsSuccess);
            Assert.Equal(calls,spy.TransitionCalls);
            IDictionary adapterSources =(IDictionary)adapter.GetType().GetField("originalChildren",Private)!.GetValue(adapter)!;
            object adapterSource =Assert.Single(adapterSources.Values.Cast<object>());
            NeutralChildDomainLease neutral =(NeutralChildDomainLease)adapterSource.GetType().GetProperty("Neutral")!.GetValue(adapterSource)!;
            Assert.False(adapter.TransitionChildDomain(neutral,NeutralChildDomainTransition.BeginDrain).IsSuccess);
            Assert.Equal(calls,spy.TransitionCalls);
            List<bool> releaseAdmissions =new List<bool>();
            foreach((object? component, string? fieldName) in new[]{((object)provider,"_originalProviderArtifacts"),((object)adapter,"originalArtifacts")})
            {
                IDictionary artifacts =(IDictionary)component.GetType().GetField(fieldName,Private)!.GetValue(component)!;
                object originalArtifact =Assert.Single(artifacts.Values.Cast<object>());
                Assert.True((bool)originalArtifact.GetType().GetProperty("ReleaseInvoked")!.GetValue(originalArtifact)!);
                PropertyInfo requestProperty =originalArtifact.GetType().GetProperty("ReleaseRequest")!; object? originalRequest =requestProperty.GetValue(originalArtifact);
                requestProperty.SetValue(originalArtifact,null);
                releaseAdmissions.Add(component==provider
                    ? provider.TransitionChildDomain(lease,PlatformChildDomainTransition.BeginDrain).IsSuccess
                    : adapter.TransitionChildDomain(neutral,NeutralChildDomainTransition.BeginDrain).IsSuccess);
                PropertyInfo invokedProperty =originalArtifact.GetType().GetProperty("ReleaseInvoked")!;
                invokedProperty.SetValue(originalArtifact,false);
                Assert.NotNull(originalArtifact.GetType().GetProperty(component==provider?"NativeReleaseRequest":"ExternalReleaseRequest")!.GetValue(originalArtifact));
                releaseAdmissions.Add(component==provider
                    ? provider.TransitionChildDomain(lease,PlatformChildDomainTransition.BeginDrain).IsSuccess
                    : adapter.TransitionChildDomain(neutral,NeutralChildDomainTransition.BeginDrain).IsSuccess);
                invokedProperty.SetValue(originalArtifact,true);
                requestProperty.SetValue(originalArtifact,originalRequest);
            }
            Assert.All(releaseAdmissions,admitted=>Assert.False(admitted));
            Assert.Equal(calls,spy.TransitionCalls);Assert.Equal(1,spy.ReleaseCalls);
            ProcessHandle independent =TestFixtures.Create(kernel,987,1987).Handle;
            OwnedBuffer<byte> foreignBuffer =kernel.AllocateBuffer<byte>(independent,16).Value!;foreignBuffer.Span[0]=0x7A;
            PlatformDomainBinding independentRoot =kernel.BindPlatformDomain(independent).Value!;
            KernelResult<PlatformChildBinding> created =bridge.CreateChildDomain(independentRoot,independentRoot.Subject,new(new(1,8192),new(authority,authority)));
            Assert.True(created.IsSuccess,created.Message); PlatformChildBinding sibling =created.Value!;
            Assert.True(bridge.TransitionChildDomain(sibling,PlatformChildDomainTransition.BeginDrain).IsSuccess);
            Assert.Equal(calls,spy.TransitionCalls); // BeginDrain is an adapter-local lifecycle effect.
            Assert.True(bridge.CloseChildDomain(sibling).IsSuccess);
            Assert.Equal(0x7A,foreignBuffer.Span[0]);Assert.True(kernel.TerminateProcess(independent).IsSuccess);
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==foreignBuffer.Handle).State);
            Assert.Equal(1,spy.ReleaseCalls);Assert.Equal(0,spy.UnmapCalls);
        }
        bool startImmediate = fault is "failed-start" or "failed-start-lost-reply" or "failed-start-refusal-history-duplicate";
        bool bindNoEffect = fault is "bind-parser-no-effect" or "bind-parser-lost-reply" or "bind-parser-reentry";
        bool bindPinned = fault is "bind-late-source-aba" or "bind-late-copy-query" or "bind-late-history-loss" or "bind-parser-source-aba" or "bind-parser-query-loss" or "bind-parser-query-generation" or "bind-parser-query-digest" or "bind-parser-copy-query" or "bind-parser-history-loss" or "bind-native-child-admission-copy" or "bind-native-mapping-admission-copy" or "bind-malformed" or "bind-history-loss" or "bind-native-history-duplicate";
        bool bindQueryBlocked = fault is "bind-query-source" or "bind-query-bridge-owner" or "bind-query-throw";
        Assert.Equal(bindNoEffect || preRefusal || fault.StartsWith("child-close-") || fault.StartsWith("parent-") || fault.StartsWith("consumed-") || fault.StartsWith("guest-proof-") || startImmediate || fault is "bind-late-reentry" or "bind-late-refusal" or "bind-late-lost-reply" or "exact" or "reentry" or "pre-effect-overlap" or "bind-lost-reply" or "bind-query-reentry" or "external-parent-close" or "guest-unmap-reentry", first.IsSuccess);
        Assert.Equal(parserRefusal || independentBefore || bindPinned || bindQueryBlocked ? 0 : 1, spy.ReleaseCalls);
        if (fault == "failed-start-release-history-loss") Assert.True(spy.NativeHistoryLost);
        Assert.Equal(first.IsSuccess || fault.StartsWith("guest-unmap-") ? 1 : 0, spy.UnmapCalls);
        if (fault.StartsWith("guest-unmap-") && !first.IsSuccess)
        {
            IDictionary nativeMappings = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("guestMappingRegistry", Private)!.GetValue(native)!;
            object nativeMapping = Assert.Single(nativeMappings.Values.Cast<object>());
            Assert.True((bool)nativeMapping.GetType().GetProperty("Closed")!.GetValue(nativeMapping)!);
            Assert.Equal(!fault.StartsWith("guest-unmap-no-effect"), nativeMapping.GetType().GetProperty("OriginalClosure")!.GetValue(nativeMapping) is not null);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1, spy.UnmapCalls);
            if (fault == "guest-unmap-independent")
            {
                IDictionary guests = (IDictionary)bridge.GetType().GetField("_originalGuestOwners", Private)!.GetValue(bridge)!;
                object ownerGuest = Assert.Single(guests.Values.Cast<object>());
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
            object guestOwner = ((IDictionary)bridge.GetType().GetField("_originalGuestOwners", Private)!.GetValue(bridge)!)[guest.MappingId]!;
            guestOwner.GetType().GetProperty("PendingSecureRegionEffects")!.SetValue(guestOwner, 0);
            Assert.True((bool)original.GetType().GetProperty("Released")!.GetValue(original)!);
            Assert.Equal(1, spy.DispositionQueries);
        }
        spy.AfterGuestUnmap = null;
        if(guestBridgeOriginal is not null){releaseBridgeSpy.BeforeSource=null;guestBridgeRoute!.SetValue(guestBridgeOwner,guestBridgeOriginal);}
        if(bridgeReleaseRaw is not null){releaseBridgeSpy.BeforeSource=null;bridgeReleaseRoute!.SetValue(bridgeReleaseOwner,bridgeReleaseRaw);}
        if(providerReleaseRaw is not null){releaseSourceSpy.BeforeSource=null;providerReleaseRoute!.SetValue(providerReleaseOwner,providerReleaseRaw);}
        if(releaseCustodyRaw is not null)releaseCustodyRoute!.SetValue(releaseCustodyOwner,releaseCustodyRaw);
        if(unmapCustodyRaw is not null)unmapCustodyRoute!.SetValue(unmapCustodyFact,unmapCustodyRaw);
        if (unmapRoutes is not null) unmapRoutes[unmapKey!] = unmapOriginal;
        if (fault.StartsWith("guest-unmap-no-effect"))
        {
            IDictionary nativeMappings = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("guestMappingRegistry", Private)!.GetValue(native)!;
            object nativeMapping = Assert.Single(nativeMappings.Values.Cast<object>());
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
        object localArtifact = ((IDictionary)bridge.GetType().GetField("_executableArtifacts", Private)!.GetValue(bridge)!)[artifact.BindingId]!;
        Assert.False((bool)localArtifact.GetType().GetProperty("ReleaseInFlight")!.GetValue(localArtifact)!);
        if (pinned)
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
            Assert.Equal(parserRefusal || independentBefore || bindPinned ? 0 : 1, spy.ReleaseCalls);
            if(fault=="history-loss")
            {
                spy.Fault="exact";
                Assert.True(bridge.UnmapChildGuestRegion(guest).IsSuccess);
                Assert.Equal(1,spy.ReleaseCalls);Assert.Equal(1,spy.UnmapCalls);
                Assert.True(bridge.TransitionChildDomain(child,PlatformChildDomainTransition.BeginDrain).IsSuccess);
                Assert.True(bridge.CloseChildDomain(child).IsSuccess);
                Assert.True(kernel.TerminateProcess(owner).IsSuccess);
                Assert.Equal(1,spy.ReleaseCalls);
                Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
                Assert.All(kernel.QueryBudget(budget).Value!.Usage,u=>Assert.Equal(0UL,u.Used));
            }
            return;
        }
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        if(unmapCustodyRaw is not null)
        {
            object cachedUnmapMapping =unmapCustodyFact!.GetType().GetProperty("Mapping")!.GetValue(unmapCustodyFact)!;
            NeutralGuestMappingLease cachedUnmapLease =(NeutralGuestMappingLease)cachedUnmapMapping.GetType().GetProperty("Neutral")!.GetValue(cachedUnmapMapping)!;
            Assert.True(adapter.ResolveOriginalGuestUnmap(cachedUnmapLease).IsSuccess);
            rootSpy.BeforeQuery=()=>
            {
                Assert.False(adapter.ResolveOriginalGuestUnmap(cachedUnmapLease).IsSuccess);
                unmapCustodyRoute!.SetValue(unmapCustodyFact,unmapCustodyRaw with {});
            };
            Assert.False(adapter.ResolveOriginalGuestUnmap(cachedUnmapLease).IsSuccess);rootSpy.BeforeQuery=null;
            unmapCustodyRoute!.SetValue(unmapCustodyFact,unmapCustodyRaw);
            Assert.True(adapter.ResolveOriginalGuestUnmap(cachedUnmapLease).IsSuccess);Assert.Equal(1,spy.UnmapCalls);
            rootSpy.ThrowSource=true;Assert.False(adapter.ResolveOriginalGuestUnmap(cachedUnmapLease).IsSuccess);rootSpy.ThrowSource=false;
            Assert.False((bool)cachedUnmapMapping.GetType().GetProperty("ClosureMayHaveEffect")!.GetValue(cachedUnmapMapping)!);
            Assert.True(adapter.ResolveOriginalGuestUnmap(cachedUnmapLease).IsSuccess);
        }
        OwnedBuffer<byte>? childCloseForeign =fault.StartsWith("child-close-canonical-",StringComparison.Ordinal)?kernel.AllocateBuffer<byte>(admin,16).Value:null;
        if(guestBridgeOriginal is not null)
        {
            Assert.True(bridge.UnmapChildGuestRegion(guest,allowCommittedClosure:true).IsSuccess);
            guestBridgeRoute!.SetValue(guestBridgeOwner,typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(guestBridgeOriginal,null));
            Assert.False(bridge.UnmapChildGuestRegion(guest,allowCommittedClosure:true).IsSuccess);
            Assert.False(bridge.CloseChildDomain(child).IsSuccess);Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1,spy.UnmapCalls);Assert.Equal(0,spy.ChildCloseCalls);Assert.Equal(0,spy.ParentCloseCalls);
            Assert.Equal(8192UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            guestBridgeRoute.SetValue(guestBridgeOwner,guestBridgeOriginal);
            Assert.True(bridge.UnmapChildGuestRegion(guest,allowCommittedClosure:true).IsSuccess);
        }
        if(childCloseForeign is not null)childCloseForeign.Span[0]=0x4D;
        OwnedBuffer<byte>? bridgeChildForeign =fault.StartsWith("child-close-bridge-",StringComparison.Ordinal)?kernel.AllocateBuffer<byte>(admin,16).Value:null;
        if(bridgeChildForeign is not null)bridgeChildForeign.Span[0]=0x3F;
        object? proofMapping=null,proofFact=null;PropertyInfo? proofRoute=null;
        if(bridgeReleaseRaw is not null)
        {
            bridgeReleaseRoute!.SetValue(bridgeReleaseOwner,null);
            bridgeReleaseOwner!.GetType().GetProperty("ReleaseResolutionObservation")!.SetValue(bridgeReleaseOwner,null);
            Assert.False(bridge.CloseChildDomain(child).IsSuccess);Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(0,spy.ChildCloseCalls);Assert.Equal(0,spy.ParentCloseCalls);
            Assert.Equal(8192UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            bridgeReleaseRoute.SetValue(bridgeReleaseOwner,bridgeReleaseRaw);
        }
        if(providerReleaseRaw is not null)
        {
            PlatformArtifactReleaseRequest providerReleaseRequest =(PlatformArtifactReleaseRequest)providerReleaseOwner!.GetType().GetProperty("ReleaseRequest")!.GetValue(providerReleaseOwner)!;
            Assert.True(provider.ResolveExecutableArtifactRelease(providerReleaseRequest).IsSuccess);
            releaseSourceSpy.BeforeSource=()=>
            {
                Assert.False(provider.ResolveExecutableArtifactRelease(providerReleaseRequest).IsSuccess);
                providerReleaseRoute!.SetValue(providerReleaseOwner,null);
                providerReleaseOwner.GetType().GetProperty("ReleaseResolutionObservation")!.SetValue(providerReleaseOwner,null);
            };
            Assert.False(provider.ResolveExecutableArtifactRelease(providerReleaseRequest).IsSuccess);releaseSourceSpy.BeforeSource=null;
            Assert.False(bridge.CloseChildDomain(child).IsSuccess);Assert.Equal(0,spy.ChildCloseCalls);
            providerReleaseRoute!.SetValue(providerReleaseOwner,providerReleaseRaw);
            Assert.True(provider.ResolveExecutableArtifactRelease(providerReleaseRequest).IsSuccess);Assert.Equal(1,spy.ReleaseCalls);
        }
        if(releaseCustodyRaw is not null)
        {
            NeutralArtifactReleaseRequest releaseCustodyRequest =(NeutralArtifactReleaseRequest)releaseCustodyOwner!.GetType().GetProperty("ReleaseRequest")!.GetValue(releaseCustodyOwner)!;
            Assert.True(adapter.ResolveExecutableArtifactRelease(releaseCustodyRequest).IsSuccess);
            rootSpy.BeforeQuery=()=>
            {
                Assert.False(adapter.ResolveExecutableArtifactRelease(releaseCustodyRequest).IsSuccess);
                releaseCustodyRoute!.SetValue(releaseCustodyOwner,null);
            };
            Assert.False(adapter.ResolveExecutableArtifactRelease(releaseCustodyRequest).IsSuccess);rootSpy.BeforeQuery=null;
            Assert.False(bridge.CloseChildDomain(child).IsSuccess);Assert.Equal(0,spy.ChildCloseCalls);
            releaseCustodyRoute!.SetValue(releaseCustodyOwner,releaseCustodyRaw);
            Assert.True(adapter.ResolveExecutableArtifactRelease(releaseCustodyRequest).IsSuccess);Assert.Equal(1,spy.ReleaseCalls);
        }
        if(fault.StartsWith("guest-proof-",StringComparison.Ordinal))
        {
            proofMapping=Assert.Single(((IDictionary)adapter.GetType().GetField("originalMappings",Private)!.GetValue(adapter)!).Values.Cast<object>());
            proofRoute=proofMapping.GetType().GetProperty("OriginalUnmap")!;proofFact=proofRoute.GetValue(proofMapping)!;
            proofRoute.SetValue(proofMapping,fault.EndsWith("loss",StringComparison.Ordinal)?null:typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(proofFact,null));
            Assert.False(bridge.CloseChildDomain(child).IsSuccess);Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(0,spy.ChildCloseCalls);Assert.Equal(0,spy.ParentCloseCalls);
            Assert.Equal(8192UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            proofRoute.SetValue(proofMapping,proofFact);
        }
        if (fault.StartsWith("child-close-"))
        {
            IDictionary adapterChildren = (IDictionary)adapter.GetType().GetField("children", Private)!.GetValue(adapter)!;
            object adapterChildKey = Assert.Single(adapterChildren.Keys.Cast<object>());
            object adapterOriginalChild = adapterChildren[adapterChildKey]!;
            ExternalGuestMappingLease? independentNativeMapping = null;
            if (fault.StartsWith("child-close-no-effect", StringComparison.Ordinal))
            {
                ExternalChildDomainLease nativeChildLease = (ExternalChildDomainLease)adapterOriginalChild.GetType().GetProperty("External")!.GetValue(adapterOriginalChild)!;
                independentNativeMapping = native.MapChildGuestMemory(nativeChildLease, new(0, 4096, new(new(Guid.NewGuid()), new(900)))).Receipt!.Mapping;
            }
            spy.AfterChildClose = () =>
            {
                foreach ((object? component, string? field) in new[] { ((object)bridge, "_secureDomainLifecycleGate"), ((object)provider, "_childCreateCommitGate"), ((object)adapter, "sync") })
                    Assert.False(Monitor.IsEntered(component.GetType().GetField(field, Private)!.GetValue(component)!));
                if (fault == "child-close-owner") adapterChildren[adapterChildKey] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(adapterOriginalChild, null)!;
                if (fault == "child-close-reentry")
                { Assert.False(bridge.CloseChildDomain(child).IsSuccess); Assert.False(kernel.TerminateProcess(owner).IsSuccess); }
            };
            object? childCustodyFact=null;FieldInfo? childCustodyRoute=null;ExternalOriginalChildCloseObservation? childCustodyRaw=null;
            spy.AfterChildQuery = () =>
            {
                if(fault.StartsWith("child-close-canonical-",StringComparison.Ordinal))rootSpy.BeforeQuery=()=>
                {
                    if(childCustodyRaw is not null)return;
                    childCustodyFact=adapterOriginalChild.GetType().GetProperty("OriginalCloseInvocation")!.GetValue(adapterOriginalChild)!;
                    childCustodyRoute=childCustodyFact.GetType().GetField("Validated")!;
                    childCustodyRaw=(ExternalOriginalChildCloseObservation)childCustodyRoute.GetValue(childCustodyFact)!;Assert.NotNull(childCustodyRaw);
                    Assert.False(Monitor.IsEntered(adapter.GetType().GetField("sync",Private)!.GetValue(adapter)!));
                    childCustodyRoute.SetValue(childCustodyFact,fault.EndsWith("loss",StringComparison.Ordinal)?null:childCustodyRaw with {});
                };
                if (fault == "child-close-query-throw") throw new IOException("pure child closure lookup interrupted");
                if (fault is "child-close-source" or "child-close-known-history-loss")
                    rootSpy.BeforeQuery = () =>
                    {
                        rootSpy.ThrowSource = true;
                        if (fault == "child-close-known-history-loss") spy.ClearNativeChildCloseHistory();
                    };
            };
            object bridgeChild = ((IDictionary)bridge.GetType().GetField("_childBindings", Private)!.GetValue(bridge)!)[child.BindingId]!;
            object? bridgeChildClosureOriginal=null; PropertyInfo bridgeChildClosureRoute =bridgeChild.GetType().GetProperty("LastClosureReceipt")!;
            if(fault.StartsWith("child-close-bridge-",StringComparison.Ordinal))releaseBridgeSpy.BeforeSource=()=>
            {
                if(bridgeChildClosureOriginal is not null || bridgeChildClosureRoute.GetValue(bridgeChild) is not { } returnedChildClosure)return;
                bridgeChildClosureOriginal=returnedChildClosure;
                Assert.False(Monitor.IsEntered(bridge.GetType().GetField("_secureDomainLifecycleGate",Private)!.GetValue(bridge)!));
                bridgeChildClosureRoute.SetValue(bridgeChild,fault.EndsWith("loss",StringComparison.Ordinal)?null:typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(returnedChildClosure,null));
            };
            if (fault == "child-close-revision-exhaustion") bridgeChild.GetType().GetField("_lifecycleRevision", Private)!.SetValue(bridgeChild, long.MaxValue);
            KernelResult childClosed = bridge.CloseChildDomain(child);
            Assert.Null(spy.CallbackFailure);
            Assert.Equal(fault == "child-close-reentry", childClosed.IsSuccess);
            IDictionary nativeChildren = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(native)!;
            object originalChild = Assert.Single(nativeChildren.Values.Cast<object>());
            Assert.Equal(fault.StartsWith("child-close-no-effect", StringComparison.Ordinal) ? ExternalChildDomainState.Ready : ExternalChildDomainState.Closed, originalChild.GetType().GetProperty("State")!.GetValue(originalChild));
            Assert.Equal(!fault.StartsWith("child-close-no-effect", StringComparison.Ordinal), originalChild.GetType().GetProperty("OriginalClosure")!.GetValue(originalChild) is not null);
            if(bridgeChildForeign is not null)Assert.Equal(0x3F,bridgeChildForeign.Span[0]);
            if (fault == "child-close-independent")
            {
                PropertyInfo state = bridgeChild.GetType().GetProperty("State")!;
                state.SetValue(bridgeChild, state.GetValue(bridgeChild));
            }
            bool childPinned = fault is "child-close-history-loss" or "child-close-independent" or "child-close-revision-exhaustion";
            spy.AfterChildClose = null; spy.AfterChildQuery = null; rootSpy.BeforeQuery = null; rootSpy.ThrowSource = false; adapterChildren[adapterChildKey] = adapterOriginalChild;
            if(bridgeChildClosureOriginal is not null){releaseBridgeSpy.BeforeSource=null;bridgeChildClosureRoute.SetValue(bridgeChild,bridgeChildClosureOriginal);}
            if(childCustodyRaw is not null)childCustodyRoute!.SetValue(childCustodyFact,childCustodyRaw);
            if (independentNativeMapping is { } nativeMapping)
            {
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                Assert.Equal(ExternalRuntimeOutcome.Closed, native.UnmapChildGuestMemory(nativeMapping, new(new(Guid.NewGuid()), new(901))).Outcome);
                if (fault == "child-close-no-effect-copy")
                {
                    object prior = originalChild.GetType().GetProperty("OriginalClose")!.GetValue(originalChild)!;
                    PropertyInfo slot = prior.GetType().GetProperty("Observation")!;
                    var noEffect = (ExternalOriginalChildCloseObservation)slot.GetValue(prior)!;
                    Assert.True(noEffect.NoEffect);
                    slot.SetValue(prior, noEffect with { });
                    Assert.Null(native.CloseOriginalChild(new(noEffect.Request.Admission, new(new(Guid.NewGuid()), new(902)))));
                    Assert.Equal(ExternalRuntimeOutcome.Unknown, native.CloseChildDomain(noEffect.Request.Admission.Lease, new(new(Guid.NewGuid()), new(903))).Outcome);
                    Assert.Same(prior, originalChild.GetType().GetProperty("OriginalClose")!.GetValue(originalChild));
                    Assert.Equal(ExternalChildDomainState.Ready, originalChild.GetType().GetProperty("State")!.GetValue(originalChild));
                    Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
                    slot.SetValue(prior, noEffect);
                }
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
            Assert.Equal(fault.StartsWith("child-close-no-effect", StringComparison.Ordinal) ? 2 : 1, spy.ChildCloseCalls);
            if(bridgeChildClosureOriginal is not null)
            {
                int localChildConsumption =0;
                Assert.False(bridge.CloseChildDomain(child,allowCommittedClosure:true,cleanupCompletionConsumer:commit=>
                {
                    bridgeChildClosureRoute.SetValue(bridgeChild,typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(bridgeChildClosureOriginal,null));
                    return commit(()=>{localChildConsumption++;return KernelResult.Ok();});
                }).IsSuccess);
                Assert.Equal(0,localChildConsumption);bridgeChildClosureRoute.SetValue(bridgeChild,bridgeChildClosureOriginal);
                Assert.True(bridge.CloseChildDomain(child,allowCommittedClosure:true,cleanupCompletionConsumer:commit=>commit(()=>{localChildConsumption++;return KernelResult.Ok();})).IsSuccess);
                Assert.Equal(1,localChildConsumption);Assert.Equal(1,spy.ChildCloseCalls);
                IDictionary bridgeChildRoutes =(IDictionary)bridge.GetType().GetField("_childBindings",Private)!.GetValue(bridge)!;
                bridgeChildRoutes[child.BindingId]=typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(bridgeChild,null);
                Assert.False(bridge.CloseChildDomain(child,allowCommittedClosure:true,cleanupCompletionConsumer:commit=>commit(()=>{localChildConsumption++;return KernelResult.Ok();})).IsSuccess);
                Assert.Equal(1,localChildConsumption);bridgeChildRoutes[child.BindingId]=bridgeChild;
            }
            if(childCustodyRaw is not null)
            {
                NeutralChildDomainLease consumedChildLease =(NeutralChildDomainLease)adapterOriginalChild.GetType().GetProperty("Neutral")!.GetValue(adapterOriginalChild)!;
                Assert.True(adapter.ResolveOriginalChildClose(consumedChildLease).IsSuccess);
                rootSpy.BeforeQuery=()=>
                {
                    Assert.False(adapter.ResolveOriginalChildClose(consumedChildLease).IsSuccess);
                    childCustodyRoute!.SetValue(childCustodyFact,childCustodyRaw with {});
                };
                Assert.False(adapter.ResolveOriginalChildClose(consumedChildLease).IsSuccess);rootSpy.BeforeQuery=null;
                childCustodyRoute!.SetValue(childCustodyFact,childCustodyRaw);
                rootSpy.ThrowSource=true;Assert.False(adapter.ResolveOriginalChildClose(consumedChildLease).IsSuccess);rootSpy.ThrowSource=false;
                Assert.False((bool)adapterOriginalChild.GetType().GetProperty("CloseMayHaveEffect")!.GetValue(adapterOriginalChild)!);
                Assert.True(adapter.ResolveOriginalChildClose(consumedChildLease).IsSuccess);Assert.Equal(1,spy.ChildCloseCalls);
                Assert.Equal(0x4D,childCloseForeign!.Span[0]);
            }
        }
        else { KernelResult recoveredChildClose =bridge.CloseChildDomain(child);Assert.True(recoveredChildClose.IsSuccess,recoveredChildClose.Message);}
        IDictionary parentRoutes = (IDictionary)adapter.GetType().GetField("parents", Private)!.GetValue(adapter)!;
        object parentKey = Assert.Single(parentRoutes.Keys.Cast<object>());
        object parentOriginal = parentRoutes[parentKey]!;
        if(guestBridgeOriginal is not null)
        {
            guestBridgeRoute!.SetValue(guestBridgeOwner,null);Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1,spy.UnmapCalls);Assert.Equal(1,spy.ChildCloseCalls);Assert.Equal(0,spy.ParentCloseCalls);
            guestBridgeRoute.SetValue(guestBridgeOwner,guestBridgeOriginal);
            bool lateGuestRootFault =false;
            releaseBridgeSpy.BeforeSource=()=>
            {
                if(lateGuestRootFault)return;
                object guestRootProvider =Assert.Single(((IDictionary)provider.GetType().GetField("_domains",Private)!.GetValue(provider)!).Values.Cast<object>());
                if(!(bool)guestRootProvider.GetType().GetProperty("Revoked")!.GetValue(guestRootProvider)!)return;
                lateGuestRootFault=true;guestBridgeRoute.SetValue(guestBridgeOwner,null);
            };
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.True(lateGuestRootFault);
            Assert.Equal(1,spy.UnmapCalls);Assert.Equal(1,spy.ChildCloseCalls);Assert.Equal(1,spy.ParentCloseCalls);
            Assert.Equal(8192UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            releaseBridgeSpy.BeforeSource=null;guestBridgeRoute.SetValue(guestBridgeOwner,guestBridgeOriginal);
        }
        if(bridgeChildForeign is not null)
        {
            object completedBridgeChild =((IDictionary)bridge.GetType().GetField("_childBindings",Private)!.GetValue(bridge)!)[child.BindingId]!;
            PropertyInfo completedBridgeRoute =completedBridgeChild.GetType().GetProperty("LastClosureReceipt")!; object completedBridgeRaw =completedBridgeRoute.GetValue(completedBridgeChild)!;
            completedBridgeRoute.SetValue(completedBridgeChild,null);Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(8192UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(1,spy.ChildCloseCalls);Assert.Equal(0,spy.ParentCloseCalls);Assert.Equal(0x3F,bridgeChildForeign.Span[0]);
            completedBridgeRoute.SetValue(completedBridgeChild,completedBridgeRaw);
            bool lateChildRootFault =false;
            releaseBridgeSpy.BeforeSource=()=>
            {
                if(lateChildRootFault)return;
                object childRootProvider =Assert.Single(((IDictionary)provider.GetType().GetField("_domains",Private)!.GetValue(provider)!).Values.Cast<object>());
                if(!(bool)childRootProvider.GetType().GetProperty("Revoked")!.GetValue(childRootProvider)!)return;
                lateChildRootFault=true;completedBridgeRoute.SetValue(completedBridgeChild,null);
            };
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.True(lateChildRootFault);
            Assert.Equal(1,spy.ChildCloseCalls);Assert.Equal(1,spy.ParentCloseCalls);
            Assert.Equal(8192UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            releaseBridgeSpy.BeforeSource=null;completedBridgeRoute.SetValue(completedBridgeChild,completedBridgeRaw);
        }
        if(childCloseForeign is not null)
        {
            object parentChildSource =Assert.Single(((IDictionary)adapter.GetType().GetField("originalChildren",Private)!.GetValue(adapter)!).Values.Cast<object>());
            object parentChildClose =parentChildSource.GetType().GetProperty("OriginalCloseInvocation")!.GetValue(parentChildSource)!;
            FieldInfo parentChildCanonicalRoute =parentChildClose.GetType().GetField("Validated")!; object? parentChildCanonical =parentChildCanonicalRoute.GetValue(parentChildClose);
            parentChildCanonicalRoute.SetValue(parentChildClose,null);Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1,spy.ChildCloseCalls);Assert.Equal(0,spy.ParentCloseCalls);Assert.Equal(0x4D,childCloseForeign.Span[0]);
            Assert.Equal(8192UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            parentChildCanonicalRoute.SetValue(parentChildClose,parentChildCanonical);
        }
        if(bridgeReleaseRaw is not null)
        {
            bridgeReleaseRoute!.SetValue(bridgeReleaseOwner,null);Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1,spy.ChildCloseCalls);Assert.Equal(0,spy.ParentCloseCalls);Assert.Equal(1,spy.ReleaseCalls);
            bridgeReleaseRoute.SetValue(bridgeReleaseOwner,bridgeReleaseRaw);
            bool lateBridgeReleaseFault =false;
            releaseBridgeSpy.BeforeSource=()=>
            {
                if(lateBridgeReleaseFault)return;lateBridgeReleaseFault=true;
                bridgeReleaseRoute.SetValue(bridgeReleaseOwner,null);
            };
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.True(lateBridgeReleaseFault);
            Assert.Equal(8192UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            releaseBridgeSpy.BeforeSource=null;bridgeReleaseRoute.SetValue(bridgeReleaseOwner,bridgeReleaseRaw);
        }
        if(providerReleaseRaw is not null)
        {
            providerReleaseRoute!.SetValue(providerReleaseOwner,null);
            providerReleaseOwner!.GetType().GetProperty("ReleaseResolutionObservation")!.SetValue(providerReleaseOwner,null);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(0,spy.ParentCloseCalls);Assert.Equal(8192UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            providerReleaseRoute.SetValue(providerReleaseOwner,providerReleaseRaw);
        }
        if(releaseForeign is not null)Assert.Equal(0x5E,releaseForeign.Span[0]);
        if(releaseCustodyRaw is not null)
        {
            releaseCustodyRoute!.SetValue(releaseCustodyOwner,null);Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(0,spy.ParentCloseCalls);Assert.Equal(8192UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            releaseCustodyRoute.SetValue(releaseCustodyOwner,releaseCustodyRaw);
        }
        if(unmapForeign is not null)Assert.Equal(0x6C,unmapForeign.Span[0]);
        if(proofRoute is not null)
        {
            proofRoute.SetValue(proofMapping,fault.EndsWith("loss",StringComparison.Ordinal)?null:typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(proofFact,null));
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(1,spy.ChildCloseCalls);Assert.Equal(0,spy.ParentCloseCalls);
            proofRoute.SetValue(proofMapping,proofFact);
        }
        PropertyInfo canonicalRoute =parentOriginal.GetType().GetProperty("ValidatedObservation")!;
        ExternalOriginalDomainCloseObservation? custodyCanonical=null;
        OwnedBuffer<byte> custodyForeignRegion =kernel.AllocateBuffer<byte>(admin,16).Value!;custodyForeignRegion.Span[0]=0x7B;
        object? childClosureOwner=null,childClosureOriginal=null;PropertyInfo? childClosureRoute=null;
        if(fault.StartsWith("parent-child-closure-",StringComparison.Ordinal))
        {
            childClosureOwner=Assert.Single(((IDictionary)adapter.GetType().GetField("originalChildren",Private)!.GetValue(adapter)!).Values.Cast<object>());
            childClosureRoute=childClosureOwner.GetType().GetProperty("OriginalCloseInvocation")!;childClosureOriginal=childClosureRoute.GetValue(childClosureOwner);Assert.NotNull(childClosureOriginal);
            childClosureRoute.SetValue(childClosureOwner,fault.EndsWith("loss",StringComparison.Ordinal)?null:typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(childClosureOriginal,null));
        }
        ExternalChildDomainLease? extraChild = null;
        if (fault.StartsWith("parent-no-effect", StringComparison.Ordinal))
        {
            ExternalDomainLease nativeParent = (ExternalDomainLease)parentOriginal.GetType().GetProperty("External")!.GetValue(parentOriginal)!;
            extraChild = native.CreateChildDomain(nativeParent, new(Guid.NewGuid(), ExternalChildAuthority.Execute, 4096, new(new(Guid.NewGuid()), new(400)))).Receipt!.Lease;
        }
        object? nativeCloseFact = null;
        PropertyInfo? nativeCloseSlot = null;
        ExternalOriginalDomainCloseObservation? nativeCloseOriginal = null;
        spy.AfterParentClose = () =>
        {
            foreach ((object? component, string? field) in new[] { ((object)bridge, "_secureDomainLifecycleGate"), ((object)provider, "_childCreateCommitGate"), ((object)adapter, "sync") })
                Assert.False(Monitor.IsEntered(component.GetType().GetField(field, Private)!.GetValue(component)!));
            if (fault == "parent-owner") parentRoutes[parentKey] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(parentOriginal, null)!;
            if (fault == "parent-reentry") Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            if (fault.StartsWith("parent-native-raw-", StringComparison.Ordinal))
            {
                object rootOwner = Assert.Single(((IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(native)!).Values.Cast<object>());
                nativeCloseFact = rootOwner.GetType().GetProperty("OriginalClose")!.GetValue(rootOwner)!;
                nativeCloseSlot = nativeCloseFact.GetType().GetProperty("Observation")!;
                nativeCloseOriginal = (ExternalOriginalDomainCloseObservation)nativeCloseSlot.GetValue(nativeCloseFact)!;
                Assert.NotNull(nativeCloseOriginal);
                nativeCloseSlot.SetValue(nativeCloseFact, fault.EndsWith("loss", StringComparison.Ordinal) ? null : nativeCloseOriginal with { });
            }
        };
        spy.AfterParentQuery = () =>
        {
            if (fault == "parent-query-throw") throw new IOException("pure original lookup interrupted");
            if(fault.StartsWith("parent-canonical-",StringComparison.Ordinal))
                rootSpy.BeforeQuery=()=>
                {
                    if(custodyCanonical is not null)return;
                    custodyCanonical=(ExternalOriginalDomainCloseObservation)canonicalRoute.GetValue(parentOriginal)!;
                    Assert.NotNull(custodyCanonical);
                    Assert.False(Monitor.IsEntered(adapter.GetType().GetField("sync",Private)!.GetValue(adapter)!));
                    Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                    canonicalRoute.SetValue(parentOriginal,fault.EndsWith("loss",StringComparison.Ordinal)?null:custodyCanonical with {});
                };
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
            IDictionary nativeParents = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(native)!;
            Assert.Single(nativeParents.Values.Cast<object>()).GetType().GetProperty("OriginalClose")!.SetValue(Assert.Single(nativeParents.Values.Cast<object>()), null);
        }
        if (fault == "parent-pre-source") rootSpy.ThrowSource = true;
        KernelResult terminal = kernel.TerminateProcess(owner);
        Assert.Null(spy.CallbackFailure);
        if (fault.StartsWith("parent-") && fault != "parent-reentry")
        {
            Assert.False(terminal.IsSuccess);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(fault == "parent-pre-source" || fault.StartsWith("parent-child-closure-",StringComparison.Ordinal) ? 0 : 1, spy.ParentCloseCalls);
            if(childClosureRoute is not null)
            {
                Assert.Equal(0,model.ActiveBindingCount);Assert.Equal(0x7B,custodyForeignRegion.Span[0]);
                childClosureRoute.SetValue(childClosureOwner,childClosureOriginal);
            }
            if(fault.StartsWith("parent-canonical-",StringComparison.Ordinal))
            {
                Assert.NotNull(custodyCanonical);Assert.Equal(0,model.ActiveBindingCount);
                object closedCustodyParent =Assert.Single(((IDictionary)native.GetType().GetField("registry",Private)!.GetValue(native)!).Values.Cast<object>());
                Assert.Equal("Closed",closedCustodyParent.GetType().GetProperty("Lifecycle")!.GetValue(closedCustodyParent)!.ToString());
                Assert.Null(closedCustodyParent.GetType().GetProperty("Binding")!.GetValue(closedCustodyParent));Assert.Null(closedCustodyParent.GetType().GetProperty("Context")!.GetValue(closedCustodyParent));
                Assert.Equal(0x7B,custodyForeignRegion.Span[0]);
                canonicalRoute.SetValue(parentOriginal,custodyCanonical);
            }
            spy.AfterParentClose = null; spy.AfterParentQuery = null; rootSpy.BeforeQuery = null; rootSpy.ThrowSource = false; parentRoutes[parentKey] = parentOriginal;
            if (nativeCloseOriginal is not null)
            {
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                Assert.Equal(1, spy.ParentCloseCalls);
                Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
                nativeCloseSlot!.SetValue(nativeCloseFact, nativeCloseOriginal);
            }
            if (fault == "parent-pre-source")
            {
                IDictionary originalRoots = (IDictionary)bridge.GetType().GetField("_domains", Private)!.GetValue(bridge)!;
                object rootOwner = originalRoots[binding.BindingId]!;
                PlatformProviderDomainLease providerLease = (PlatformProviderDomainLease)rootOwner.GetType().GetProperty("ProviderLease")!.GetValue(rootOwner)!;
                Assert.False(provider.ResolveDomainClosure(providerLease).IsSuccess);
                Assert.Equal(0, spy.ParentCloseCalls); // Pure lookup cannot create the remaining effect.
            }
            if (extraChild is { } dependent)
            {
                Assert.Equal(ExternalRuntimeOutcome.Closed, native.CloseChildDomain(dependent, new(new(Guid.NewGuid()), new(401))).Outcome);
                if (fault == "parent-no-effect-copy")
                {
                    object rootOwner = Assert.Single(((IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(native)!).Values.Cast<object>());
                    object prior = rootOwner.GetType().GetProperty("OriginalClose")!.GetValue(rootOwner)!;
                    PropertyInfo slot = prior.GetType().GetProperty("Observation")!;
                    var originalNoEffect = (ExternalOriginalDomainCloseObservation)slot.GetValue(prior)!;
                    Assert.True(originalNoEffect.NoEffect);
                    slot.SetValue(prior, originalNoEffect with { });
                    Assert.Null(native.CloseOriginalDomain(new(originalNoEffect.Request.Admission, new(new(Guid.NewGuid()), new(402)))));
                    Assert.Same(prior, rootOwner.GetType().GetProperty("OriginalClose")!.GetValue(rootOwner));
                    Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
                    slot.SetValue(prior, originalNoEffect);
                }
            }
            terminal = kernel.TerminateProcess(owner);
            Assert.Equal(fault.StartsWith("parent-no-effect", StringComparison.Ordinal) ? 2 : 1, spy.ParentCloseCalls);
            Assert.False((bool)parentOriginal.GetType().GetProperty("CloseInFlight")!.GetValue(parentOriginal)!);
            if (fault == "parent-history-loss")
            {
                Assert.False(terminal.IsSuccess);
                Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
                return;
            }
        }
        if (fault == "child-close-known-history-loss")
        {
            Assert.False(terminal.IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1, spy.ChildCloseCalls);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(8192UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            return; // No exact native close fact survives; root pins remain mandatory.
        }
        Assert.True(terminal.IsSuccess, terminal.Message);
        if(guestBridgeOriginal is not null)Assert.Equal(0x6C,unmapForeign!.Span[0]);
        if(bridgeChildForeign is not null)Assert.Equal(0x3F,bridgeChildForeign.Span[0]);
        if(childCloseForeign is not null)Assert.Equal(0x4D,childCloseForeign.Span[0]);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.Equal(0, model.ActiveBindingCount);
        if(fault.StartsWith("parent-canonical-",StringComparison.Ordinal))
        {
            NeutralExternalParentCloseRequest custodyRequest =(NeutralExternalParentCloseRequest)parentOriginal.GetType().GetProperty("CloseRequest")!.GetValue(parentOriginal)!;
            Assert.True(adapter.ResolveExternalParentClose(custodyRequest).IsSuccess);
            canonicalRoute.SetValue(parentOriginal,custodyCanonical! with {});Assert.False(adapter.ResolveExternalParentClose(custodyRequest).IsSuccess);
            canonicalRoute.SetValue(parentOriginal,custodyCanonical);
            rootSpy.BeforeQuery=()=>canonicalRoute.SetValue(parentOriginal,custodyCanonical! with {});
            Assert.False(adapter.ResolveExternalParentClose(custodyRequest).IsSuccess);
            rootSpy.BeforeQuery=null;canonicalRoute.SetValue(parentOriginal,custodyCanonical);
            parentRoutes[parentKey]=typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(parentOriginal,null)!;
            Assert.False(adapter.ResolveExternalParentClose(custodyRequest).IsSuccess);parentRoutes[parentKey]=parentOriginal;
            Assert.True(adapter.ResolveExternalParentClose(custodyRequest).IsSuccess);Assert.Equal(1,spy.ParentCloseCalls);Assert.Equal(0x7B,custodyForeignRegion.Span[0]);
        }
        if (fault == "external-parent-close" || fault.StartsWith("parent-"))
        {
            IDictionary nativeParents = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(native)!;
            Assert.All(nativeParents.Values.Cast<object>(), parentOwner =>
                Assert.Equal("Closed", parentOwner.GetType().GetProperty("Lifecycle")!.GetValue(parentOwner)!.ToString()));
        }
    }

    public interface IReleaseBridgeProvider : ExactVirtualIoBackingConsumerTests.IBridgeBindProvider,IPlatformChildDomainProvider,IPlatformGuestMemoryProvider,
        IPlatformChildExecutionProvider,IPlatformOriginalChildCloseProvider,IPlatformExecutableArtifactReleaseProvider,IPlatformOriginalArtifactBindProvider,
        IPlatformOriginalArtifactBindNoEffectProvider,IPlatformDomainRevocationNoEffectProvider,IPlatformRegionRevocationNoEffectProvider,IPlatformOriginalGuestUnmapProvider { }
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
                ExternalOriginalDomainBindRequest prepared = (ExternalOriginalDomainBindRequest)args![0]!;
                if (Fault == "external-parent-bind-foreign-source")
                { Foreign = new(); IndependentRoot = Foreign.BindDomain(new(Guid.NewGuid(), ExternalDomainProfile.IsolatedDomain, new(new(Guid.NewGuid()), new(900)))).Receipt!; target = Foreign; }
            }
            object? result = method!.Invoke(target, args);
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
        public int ReleaseCalls, UnmapCalls, StartCalls, MapCalls, TransitionCalls;
        public int OriginalStartCalls, OriginalStartQueries;
        public int DispositionQueries;
        public string? StartQueryFault;
        public int CreateCalls;
        public bool NativeHistoryLost;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if(method!.Name=="TransitionChildDomain")TransitionCalls++;
            if (method!.Name == nameof(IHybridCpuOriginalChildCreateRuntimeV1.CreateOriginalChild) && Fault.StartsWith("child-create-no-effect"))
            {
                ExternalOriginalChildCreateRequest request = (ExternalOriginalChildCreateRequest)args![0]!;
                ExtraChildCreate = Inner.CreateChildDomain(request.Admission.Lease, request.CreationRequest with { RequestId = Guid.NewGuid() }).Receipt!;
                Assert.NotNull(ExtraChildCreate);
            }
            if (method!.Name == nameof(IHybridCpuOriginalGuestMapRuntimeV1.MapOriginalGuest) && Fault.StartsWith("guest-map-no-effect"))
            {
                ExternalOriginalGuestMapRequest original = (ExternalOriginalGuestMapRequest)args![0]!;
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
                IDictionary nativeMappings = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("guestMappingRegistry", Private)!.GetValue(Inner)!;
                object nativeMapping = Assert.Single(nativeMappings.Values.Cast<object>());
                nativeMapping.GetType().GetProperty("Closed")!.SetValue(nativeMapping, true);
            }
            if (method.Name == nameof(IHybridCpuOriginalChildExecutionStartRuntimeV1.StartOriginalChildExecution) && Fault == "failed-start-refusal-history-duplicate")
                typeof(HybridCpuExternalRuntime).GetField("nextExecutionGeneration", Private)!.SetValue(Inner, long.MaxValue);
            object? result = method.Invoke(Inner, args);
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
                    IDictionary roots = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(Inner)!;
                    object owner = Assert.Single(roots.Values.Cast<object>());
                    IList history = (IList)owner.GetType().GetProperty("OriginalChildCreates")!.GetValue(owner)!;
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
                    IDictionary owners = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(Inner)!;
                    object owner = Assert.Single(owners.Values.Cast<object>());
                    IList rows = (IList)owner.GetType().GetProperty("OriginalGuestMaps")!.GetValue(owner)!;
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
                    IDictionary parents = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(Inner)!;
                    object original = Assert.Single(parents.Values.Cast<object>());
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
                    object owner = Assert.Single(((IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(Inner)!).Values.Cast<object>());
                    ((IList)owner.GetType().GetProperty("OriginalArtifactBinds")!.GetValue(owner)!).Clear();
                }
                if (Fault is "bind-late-lost-reply" or "bind-late-history-loss" or "bind-parser-lost-reply" or "bind-parser-history-loss") throw new IOException("Actual Bind refusal reply lost");
                if (Fault is "bind-native-child-admission-copy" or "bind-native-mapping-admission-copy")
                {
                    IDictionary registry = (IDictionary)typeof(HybridCpuExternalRuntime).GetField(Fault == "bind-native-child-admission-copy" ? "childRegistry" : "guestMappingRegistry", Private)!.GetValue(Inner)!;
                    object owner = Assert.Single(registry.Values.Cast<object>());
                    PropertyInfo admission = owner.GetType().GetProperty("OriginalAdmission")!;
                    admission.SetValue(owner, typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(admission.GetValue(owner), null));
                    throw new IOException("Bind reply lost after admission copy");
                }
                if (Fault == "bind-malformed" && result is ExternalChildResult<ExternalChildArtifactBindReceipt> binding)
                    return binding with { Receipt = binding.Receipt! with { PackageSha256 = "foreign" } };
                if (Fault == "bind-native-history-duplicate")
                {
                    IDictionary registry = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(Inner)!;
                    object owner = Assert.Single(registry.Values.Cast<object>());
                    IList history = (IList)owner.GetType().GetProperty("OriginalArtifactBinds")!.GetValue(owner)!;
                    history.Add(Assert.Single(history.Cast<object>()));
                    owner.GetType().GetProperty("ArtifactBindsIssued")!.SetValue(owner, 2UL);
                    throw new IOException("Bind reply lost after malformed native history");
                }
                if (Fault == "bind-history-loss")
                {
                    IDictionary registry = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(Inner)!;
                    object owner = Assert.Single(registry.Values.Cast<object>());
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
                IDictionary nativeOwners = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(Inner)!;
                object nativeOwner = Assert.Single(nativeOwners.Values.Cast<object>());
                PropertyInfo factProperty = nativeOwner.GetType().GetProperty(Fault == "native-release-copy" ? "ArtifactRelease" : "ArtifactBind")!;
                object originalFact = factProperty.GetValue(nativeOwner)!;
                factProperty.SetValue(nativeOwner, typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(originalFact, null));
                Assert.Null(nativeOwner.GetType().GetProperty("Execution")!.GetValue(nativeOwner)); // Actual native join finished.
                throw new IOException("Original release reply lost after native fact copy");
            }
            if (Fault == "failed-start-release-history-loss")
            {
                IDictionary children = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(Inner)!;
                object nativeChild = Assert.Single(children.Values.Cast<object>());
                nativeChild.GetType().GetProperty("ArtifactRelease")!.SetValue(nativeChild, null);
                NativeHistoryLost = true;
            }
            if (Fault is "release-query-request-copy" or "lost-reply" or "history-loss" or "failed-start-release-lost-reply" or "failed-start-release-history-loss") throw new IOException("release reply lost after actual effect");
            if (Fault == "release-request-copy")
            {
                ExternalChildResult<ExternalArtifactReleaseReceipt> raw = (ExternalChildResult<ExternalArtifactReleaseReceipt>)result!;
                return raw with { Receipt = raw.Receipt! with { Request = raw.Receipt.Request with { } } };
            }
            if (Fault == "malformed")
            {
                ExternalChildResult<ExternalArtifactReleaseReceipt> raw = (ExternalChildResult<ExternalArtifactReleaseReceipt>)result!;
                return raw with { Receipt = raw.Receipt! with { Request = raw.Receipt.Request with { Operation = new(new(Guid.NewGuid()), new(1)) } } };
            }
            return result;
        }
        public void ClearNativeGuestMapHistory()
        {
            IDictionary children = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(Inner)!;
            object child = Assert.Single(children.Values.Cast<object>());
            ((IList)child.GetType().GetProperty("OriginalGuestMaps")!.GetValue(child)!).Clear();
        }
        public void ClearNativeChildCreateHistory()
        {
            IDictionary roots = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(Inner)!;
            object owner = Assert.Single(roots.Values.Cast<object>());
            ((IList)owner.GetType().GetProperty("OriginalChildCreates")!.GetValue(owner)!).Clear();
        }
        public void ClearNativeGuestUnmapHistory()
        {
            IDictionary mappings = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("guestMappingRegistry", Private)!.GetValue(Inner)!;
            Assert.Single(mappings.Values.Cast<object>()).GetType().GetProperty("OriginalUnmap")!.SetValue(Assert.Single(mappings.Values.Cast<object>()), null);
        }
        public void ClearNativeChildCloseHistory()
        {
            IDictionary children = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(Inner)!;
            object original = Assert.Single(children.Values.Cast<object>());
            original.GetType().GetProperty("OriginalClose")!.SetValue(original, null);
        }
    }
}









