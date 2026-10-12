using System.Collections;
using System.Reflection;
using HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Tests.Platform;

public sealed class ExactNestedGuestConsumerTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public class GuestSpy : ExactNestedDomainConsumerTests.NestedSpy
    {
        public Action? BeforeMap;
        public string MapFault = "exact";
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "MapOriginalGuest") BeforeMap?.Invoke();
            var raw = base.Invoke(method, args);
            if (method.Name == "MapOriginalGuest" && MapFault == "lost-reply") throw new IOException("Map reply lost after effect.");
            if (method.Name == "MapOriginalGuest" && MapFault == "copy" && raw is ExternalOriginalGuestMapObservation observation) return observation with { };
            if (method.Name == "UnmapOriginalGuest" && MapFault == "unmap-lost-reply") throw new IOException("Unmap reply lost after effect.");
            return raw;
        }
        public object NativeParent => ((IDictionary)Field(Inner, "childRegistry"))[Original!.ParentAdmission.Lease.Handle]!;
    }
    [Theory]
    [InlineData("exact")] [InlineData("native-history-before")] [InlineData("native-history-after")]
    [InlineData("sdk-history")] [InlineData("provider-history")] [InlineData("sdk-aba")]
    [InlineData("bridge-aba")] [InlineData("reentry")] [InlineData("lost-reply")] [InlineData("copy")]
    [InlineData("no-effect")] [InlineData("unmap-lost-reply")] [InlineData("guest-capacity")]
    public void ActualNestedGuestMapRequiresOriginalLineageAndKeepsBackingUntilExactClosure(string mode)
    {
        var native = DispatchProxy.Create<ExactNestedDomainConsumerTests.INestedRuntime, GuestSpy>();
        var spy = (GuestSpy)(object)native; spy.MapFault = mode;
        var sdk = new HybridCpuExecutableChildAdapter(spy.Inner, native);
        var provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), sdk);
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 786, 1130);
        var admin = TestFixtures.Create(kernel, 99, 999).Handle;
        var budgetCap = kernel.MintCapability(new(999), admin, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "nested-guest", [new(ServiceBudgetDimension.OwnedMemoryBytes, 512), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 512), new(ServiceBudgetDimension.RegionUses, 8), new(ServiceBudgetDimension.GuestMemoryBytes, mode == "guest-capacity" ? 511UL : 512UL)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var root = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, root.Domain, root.ConfigureCapability).IsSuccess);
        var nested = kernel.CreateNestedVirtualDomain(owner, root.Domain, root.ConfigureCapability,
            new(new(1, 1024), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.GuestMemory)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, nested.Domain, nested.ConfigureCapability).IsSuccess);
        var memory = kernel.MintCapability(process.DomainId, owner, ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        if (mode == "native-history-before") spy.BeforeMap = () => Clear(spy.NativeParent, "OriginalNestedChildren");
        if (mode == "no-effect") spy.BeforeMap = () => Set(spy.NativeParent, "State", (ExternalChildDomainState)255);
        spy.After = name =>
        {
            if (name != "MapOriginalGuest") return;
            Assert.False(Monitor.IsEntered(Field(sdk, "sync")));
            Assert.False(Monitor.IsEntered(Field(provider, "_childCreateCommitGate")));
            Assert.False(Monitor.IsEntered(Field(kernel.PlatformAuthority, "_secureDomainLifecycleGate")));
            if (mode == "native-history-after") Clear(spy.NativeParent, "OriginalNestedChildren");
            if (mode == "no-effect") Set(spy.NativeParent, "State", ExternalChildDomainState.Ready);
            if (mode is "sdk-history" or "sdk-aba")
            {
                var parent = ((IDictionary)Field(sdk, "originalChildren")).Values.Cast<object>().Single(x => Property(x, "NestedParent") is null);
                if (mode == "sdk-history") Clear(parent, "OriginalNested");
                else { Set(parent, "State", NeutralChildDomainState.Parked); Set(parent, "State", NeutralChildDomainState.Created); }
            }
            if (mode == "provider-history")
                Clear(((IDictionary)Field(provider, "_originalProviderChildren")).Values.Cast<object>().Single(x => Property(x, "NestedParent") is null), "OriginalNested");
            if (mode == "bridge-aba")
            {
                var parent = ((IDictionary)Field(kernel.PlatformAuthority, "_originalChildOwners")).Values.Cast<object>().Single(x => Property(x, "ParentChild") is null);
                Set(parent, "State", global::SingPlus.Platform.PlatformChildDomainState.Draining);
                Set(parent, "State", global::SingPlus.Platform.PlatformChildDomainState.Created);
            }
            if (mode == "reentry") Assert.False(kernel.DestroyVirtualDomain(owner, root.Domain, root.ConfigureCapability).IsSuccess);
        };
        var mapped = kernel.MapGuestRegion(owner, nested.Domain, nested.MemoryCapability, memory, region.Handle, new(0, 512), GuestMemoryAccess.Read);
        if (mode == "guest-capacity")
        {
            Assert.False(mapped.IsSuccess);
            Assert.Equal(0, spy.Calls.GetValueOrDefault("MapOriginalGuest"));
            Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.GuestMemoryBytes).Used);
            Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.All(kernel.QueryBudget(budget).Value!.Usage, usage => Assert.Equal(0UL, usage.Used));
            return;
        }
        Assert.Equal(mode is "bridge-aba" or "reentry" or "no-effect" ? 0UL : 512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.GuestMemoryBytes).Used);
        Assert.Equal(mode is "exact" or "unmap-lost-reply", mapped.IsSuccess); // Reentry may retire permission, so final publication must refuse.
        Assert.Equal(1, spy.Calls.GetValueOrDefault("MapOriginalGuest"));
        if (mode is "exact" or "unmap-lost-reply")
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        spy.After = null; spy.BeforeMap = null;
        bool pinned = mode is not ("exact" or "reentry" or "lost-reply" or "bridge-aba" or "no-effect" or "unmap-lost-reply");
        var terminated = kernel.TerminateProcess(owner);
        if (mode == "unmap-lost-reply")
        {
            Assert.False(terminated.IsSuccess);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.GuestMemoryBytes).Used);
            spy.MapFault = "exact"; terminated = kernel.TerminateProcess(owner);
        }
        if (pinned)
        {
            Assert.False(terminated.IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.GuestMemoryBytes).Used);
        }
        else
        {
            Assert.True(terminated.IsSuccess, terminated.Message + " Calls=" + global::System.Text.Json.JsonSerializer.Serialize(spy.Calls));
            Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.All(kernel.QueryBudget(budget).Value!.Usage, usage => Assert.Equal(0UL, usage.Used));
            Assert.Equal(mode == "no-effect" ? 0 : 1, spy.Calls.GetValueOrDefault("UnmapOriginalGuest"));
            Assert.Equal(2, spy.Calls.GetValueOrDefault("CloseOriginalChild"));
        }
        Assert.Equal(1, spy.Calls.GetValueOrDefault("MapOriginalGuest"));
    }
    private static object Field(object owner, string name) => owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static object? Property(object owner, string name) => owner.GetType().GetProperty(name)!.GetValue(owner);
    private static void Clear(object owner, string name) => ((IList)Property(owner, name)!).Clear();
    private static void Set(object owner, string name, object value) => owner.GetType().GetProperty(name)!.SetValue(owner, value);
}


