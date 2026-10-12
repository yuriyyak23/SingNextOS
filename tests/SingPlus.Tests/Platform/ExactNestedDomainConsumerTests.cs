using System.Collections;
using System.Reflection;
using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Tests.Platform;

public sealed class ExactNestedDomainConsumerTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public interface INestedRuntime : ExactArtifactReleaseConsumerTests.IReleaseRuntime, IHybridCpuOriginalNestedChildRuntimeV1 { }
    public class NestedSpy : DispatchProxy
    {
        public HybridCpuExternalRuntime Inner = new();
        public string Fault = "exact";
        public Dictionary<string, int> Calls = [];
        public Action<string>? After;
        public ExternalOriginalNestedChildRequest? Original;
        public ExternalOriginalChildEventRequest? IndependentEvent;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name; Calls[name] = Calls.GetValueOrDefault(name) + 1;
            if (name == "ResolveOriginalNestedChild" && Fault == "query-loss") throw new IOException("Original query lost.");
            object? raw;
            object? refusedParent = null;
            if (name == "CreateOriginalNestedChild" && Fault == "no-effect")
            {
                var request = (ExternalOriginalNestedChildRequest)args![0]!;
                var children = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(Inner)!;
                refusedParent = children[request.ParentAdmission.Lease.Handle]!;
                refusedParent.GetType().GetProperty("State")!.SetValue(refusedParent, ExternalChildDomainState.Closed);
            }
            try { raw = targetMethod.Invoke(Inner, args); }
            catch (TargetInvocationException exception) { throw exception.InnerException!; }
            finally { refusedParent?.GetType().GetProperty("State")!.SetValue(refusedParent, ExternalChildDomainState.Ready); }
            if (name == "CreateOriginalNestedChild")
            {
                Original = (ExternalOriginalNestedChildRequest)args![0]!;
                if (Fault == "independent-user" && raw is ExternalOriginalNestedChildObservation { Raw.Receipt: { } admission })
                {
                    IndependentEvent = new(admission, "independent-native-mailbox", 1, new(new(Guid.NewGuid()), new(700)));
                    var accepted = Inner.InjectOriginalChildEvent(IndependentEvent)!;
                    Assert.False(accepted.NoEffect); Assert.False(accepted.NoFurtherDelivery);
                }
                if (Fault == "root-factory-history-duplicate")
                {
                    var roots = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("registry", Private)!.GetValue(Inner)!;
                    var root = Assert.Single(roots.Values.Cast<object>());
                    var history = (IList)root.GetType().GetProperty("OriginalChildCreates")!.GetValue(root)!;
                    history.Add(Assert.Single(history.Cast<object>()));
                    root.GetType().GetProperty("OriginalChildCreatesIssued")!.SetValue(root, 2UL);
                }
                if (Fault == "nested-history-duplicate")
                {
                    var children = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(Inner)!;
                    var parent = children[Original.ParentAdmission.Lease.Handle]!;
                    var rows = (IList)parent.GetType().GetProperty("OriginalNestedChildren")!.GetValue(parent)!;
                    rows.Add(Assert.Single(rows.Cast<object>()));
                    parent.GetType().GetProperty("NestedChildrenIssued")!.SetValue(parent, 2UL);
                }
                if (Fault == "native-history")
                {
                    var children = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(Inner)!;
                    var parent = children[Original.ParentAdmission.Lease.Handle]!;
                    ((IList)parent.GetType().GetProperty("OriginalNestedChildren")!.GetValue(parent)!).Clear();
                }
            }
            After?.Invoke(name);
            if (name == "CreateOriginalNestedChild" && Fault == "create-loss") throw new IOException("Create reply lost after effect.");
            if (name == "CreateOriginalNestedChild" && Fault == "copy" && raw is ExternalOriginalNestedChildObservation observation) return observation with { };
            if (name == "CloseOriginalChild" && Fault == "close-loss") throw new IOException("Close reply lost after effect.");
            return raw;
        }
    }
    [Theory]
    [InlineData("exact")] [InlineData("create-loss")] [InlineData("query-loss")]
    [InlineData("copy")] [InlineData("native-history")] [InlineData("sdk-history")]
    [InlineData("provider-history")] [InlineData("bridge-history")] [InlineData("close-loss")]
    [InlineData("sdk-parent-copy")] [InlineData("provider-parent-copy")] [InlineData("no-effect")] [InlineData("reentry")]
    [InlineData("independent-user")]
    [InlineData("root-factory-history-duplicate")]
    [InlineData("nested-history-duplicate")]
    public void ActualNestedFactoryPublishesOrContainsOriginalAndReclaimsQuantitatively(string mode)
    {
        var native = DispatchProxy.Create<INestedRuntime, NestedSpy>(); var spy = (NestedSpy)(object)native; spy.Fault = mode;
        var sdk = new HybridCpuExecutableChildAdapter(spy.Inner, native);
        var provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), sdk);
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 785, 1129);
        var admin = TestFixtures.Create(kernel, 99, 999).Handle;
        var budgetCap = kernel.MintCapability(new(999), admin, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "external-nested", [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var root = kernel.CreateVirtualDomain(owner, create, new(1, 4096));
        Assert.True(root.IsSuccess, root.Message);
        Assert.True(kernel.ConfigureVirtualDomain(owner, root.Value!.Domain, root.Value.ConfigureCapability).IsSuccess);
        spy.After = name =>
        {
            Assert.False(Monitor.IsEntered(Field(sdk, "sync")));
            Assert.False(Monitor.IsEntered(Field(provider, "_childCreateCommitGate")));
            Assert.False(Monitor.IsEntered(Field(kernel.PlatformAuthority, "_secureDomainLifecycleGate")));
            if (name != "CreateOriginalNestedChild") return;
            if (mode == "reentry")
                Assert.False(kernel.CreateNestedVirtualDomain(owner, root.Value.Domain, root.Value.ConfigureCapability,
                    new(new(1, 1024), VirtualDomainAuthorityClass.Lifecycle)).IsSuccess);
            if (mode is "sdk-parent-copy" or "provider-parent-copy")
            {
                var holder = mode == "sdk-parent-copy" ? (object)sdk : provider;
                var original = (IDictionary)Field(holder, mode == "sdk-parent-copy" ? "originalChildren" : "_originalProviderChildren");
                var routes = (IDictionary)Field(holder, mode == "sdk-parent-copy" ? "children" : "_providerChildren");
                var key = original.Keys.Cast<object>().Single();
                var copied = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(original[key], null)!;
                original[key] = copied; routes[key] = copied;
            }
            if (mode is "sdk-history" or "provider-history" or "bridge-history")
            {
                object parent = mode switch
                {
                    "sdk-history" => ((IDictionary)Field(sdk, "originalChildren")).Values.Cast<object>().Single(),
                    "provider-history" => ((IDictionary)Field(provider, "_originalProviderChildren")).Values.Cast<object>().Single(),
                    _ => ((IDictionary)Field(kernel.PlatformAuthority, "_originalChildOwners")).Values.Cast<object>().Single()
                };
                ((IList)parent.GetType().GetProperty(mode == "bridge-history" ? "NestedCreateInvocations" : "OriginalNested")!.GetValue(parent)!).Clear();
            }
        };
        var nested = kernel.CreateNestedVirtualDomain(owner, root.Value.Domain, root.Value.ConfigureCapability,
            new(new(1, 1024), VirtualDomainAuthorityClass.Lifecycle | (mode == "independent-user" ? VirtualDomainAuthorityClass.Events : VirtualDomainAuthorityClass.None)));
        bool published = mode is "exact" or "create-loss" or "close-loss" or "reentry" or "independent-user";
        Assert.Equal(published, nested.IsSuccess);
        Assert.Equal(1, spy.Calls.GetValueOrDefault("CreateOriginalNestedChild"));
        Assert.NotNull(spy.Original);
        if (published)
            Assert.Equal(VirtualDomainState.Created, kernel.QueryVirtualDomain(owner, nested.Value!.Domain).Value);
        else Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        spy.After = null;
        if (mode == "query-loss") spy.Fault = "exact";
        var terminated = kernel.TerminateProcess(owner);
        bool pinned = mode is "nested-history-duplicate" or "root-factory-history-duplicate" or "copy" or "native-history" or "sdk-history" or "provider-history" or "bridge-history" or "sdk-parent-copy" or "provider-parent-copy" or "independent-user";
        if (pinned)
        {
            Assert.False(terminated.IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            if (mode == "independent-user")
            {
                Assert.NotNull(spy.IndependentEvent);
                Assert.False(spy.Inner.ResolveOriginalChildEvent(spy.IndependentEvent!)!.NoFurtherDelivery);
            }
            else Assert.Equal(0, spy.Calls.GetValueOrDefault("CloseOriginalChild"));
        }
        else
        {
            if (!terminated.IsSuccess && mode == "close-loss") { spy.Fault = "exact"; terminated = kernel.TerminateProcess(owner); }
            Assert.True(terminated.IsSuccess, terminated.Message + " Calls=" + global::System.Text.Json.JsonSerializer.Serialize(spy.Calls) + " Teardown=" + global::System.Text.Json.JsonSerializer.Serialize(kernel.QueryProcessTeardown(owner).Value));
            Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(mode == "no-effect" ? 1 : 2, spy.Calls.GetValueOrDefault("CloseOriginalChild"));
        }
        Assert.Equal(1, spy.Calls.GetValueOrDefault("CreateOriginalNestedChild"));
    }
    private static object Field(object owner, string name) => owner.GetType().GetField(name, Private)!.GetValue(owner)!;
}
