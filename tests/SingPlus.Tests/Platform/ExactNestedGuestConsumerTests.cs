using System.Collections;
using System.Reflection;
using HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
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
            object? raw = base.Invoke(method, args);
            if (method.Name == "MapOriginalGuest" && MapFault is "lost-reply" or "map-and-unmap-loss" or "bridge-known-copy") throw new IOException("Map reply lost after effect.");
            if (method.Name == "MapOriginalGuest" && MapFault == "copy" && raw is ExternalOriginalGuestMapObservation observation) return observation with { };
            if (method.Name == "UnmapOriginalGuest" && MapFault is "unmap-lost-reply" or "map-and-unmap-loss") throw new IOException("Unmap reply lost after effect.");
            return raw;
        }
        public object NativeParent => ((IDictionary)Field(Inner, "childRegistry"))[Original!.ParentAdmission.Lease.Handle]!;
    }
    [Theory]
    [InlineData("exact")] [InlineData("native-history-before")] [InlineData("native-history-after")]
    [InlineData("map-and-unmap-loss")]
    [InlineData("bridge-known-copy")]
    [InlineData("sdk-history")] [InlineData("provider-history")] [InlineData("sdk-aba")]
    [InlineData("bridge-aba")] [InlineData("reentry")] [InlineData("lost-reply")] [InlineData("copy")]
    [InlineData("no-effect")] [InlineData("unmap-lost-reply")] [InlineData("guest-capacity")]
    public void ActualNestedGuestMapRequiresOriginalLineageAndKeepsBackingUntilExactClosure(string mode)
    {
        ExactNestedDomainConsumerTests.INestedRuntime native = DispatchProxy.Create<ExactNestedDomainConsumerTests.INestedRuntime, GuestSpy>();
        GuestSpy spy = (GuestSpy)(object)native; spy.MapFault = mode;
        HybridCpuExecutableChildAdapter sdk = new HybridCpuExecutableChildAdapter(spy.Inner, native);
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), sdk);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 786, 1130);
        ProcessHandle admin = TestFixtures.Create(kernel, 99, 999).Handle;
        CapabilityId budgetCap = kernel.MintCapability(new(999), admin, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "nested-guest", [new(ServiceBudgetDimension.OwnedMemoryBytes, 512), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 512), new(ServiceBudgetDimension.RegionUses, 8), new(ServiceBudgetDimension.GuestMemoryBytes, mode == "guest-capacity" ? 511UL : 512UL)]).Value!.ProcessBudget;
        OwnedBuffer<byte> region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        CapabilityId create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet root = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, root.Domain, root.ConfigureCapability).IsSuccess);
        VirtualDomainAuthoritySet nested = kernel.CreateNestedVirtualDomain(owner, root.Domain, root.ConfigureCapability,
            new(new(1, 1024), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.GuestMemory)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, nested.Domain, nested.ConfigureCapability).IsSuccess);
        CapabilityId memory = kernel.MintCapability(process.DomainId, owner, ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
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
                object parent = ((IDictionary)Field(sdk, "originalChildren")).Values.Cast<object>().Single(x => Property(x, "NestedParent") is null);
                if (mode == "sdk-history") Clear(parent, "OriginalNested");
                else { Set(parent, "State", NeutralChildDomainState.Parked); Set(parent, "State", NeutralChildDomainState.Created); }
            }
            if (mode == "provider-history")
                Clear(((IDictionary)Field(provider, "_originalProviderChildren")).Values.Cast<object>().Single(x => Property(x, "NestedParent") is null), "OriginalNested");
            if (mode == "bridge-aba")
            {
                object parent = ((IDictionary)Field(kernel.PlatformAuthority, "_originalChildOwners")).Values.Cast<object>().Single(x => Property(x, "ParentChild") is null);
                Set(parent, "State", global::SingPlus.Platform.PlatformChildDomainState.Draining);
                Set(parent, "State", global::SingPlus.Platform.PlatformChildDomainState.Created);
            }
            if (mode == "reentry") Assert.False(kernel.DestroyVirtualDomain(owner, root.Domain, root.ConfigureCapability).IsSuccess);
        };
        KernelResult<GuestRegionMapping> mapped = kernel.MapGuestRegion(owner, nested.Domain, nested.MemoryCapability, memory, region.Handle, new(0, 512), GuestMemoryAccess.Read);
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
        if(mode=="exact")
        {
            foreach(object holder in new object[]{sdk,provider})
            {
                IDictionary registry =(IDictionary)Field(holder,ReferenceEquals(holder,sdk)?"originalChildren":"_originalProviderChildren");
                object childOwner =registry.Values.Cast<object>().Single(x=>((IEnumerable)Property(x,"OriginalGuestMaps")!).Cast<object>().Any());
                object fact =((IEnumerable)Property(childOwner,"OriginalGuestMaps")!).Cast<object>().Single();
                FieldInfo? field =fact.GetType().GetField("Mapping") ?? null;
                PropertyInfo? property =fact.GetType().GetProperty("Mapping");
                object originalMapping =field is not null?field.GetValue(fact)!:property!.GetValue(fact)!;
                object request =ReferenceEquals(holder,sdk)?Property(fact,"Request")!:Property(fact,"OriginalRequest")!;
                MethodInfo method =holder.GetType().GetMethod("ResolveOriginalGuestMap")!;
                object copy =typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(originalMapping,null)!;
                if(field is not null)field.SetValue(fact,copy);else property!.SetValue(fact,copy);
                object denied =method.Invoke(holder,[request])!;
                Assert.False((bool)Property(denied,"IsSuccess")!);
                Assert.Equal(1,spy.Calls.GetValueOrDefault("MapOriginalGuest"));
                if(field is not null)field.SetValue(fact,originalMapping);else property!.SetValue(fact,originalMapping);
                Assert.True((bool)Property(method.Invoke(holder,[request])!,"IsSuccess")!);
                IList history =(IList)Property(childOwner,"OriginalGuestMaps")!;
                history.Clear(); // Issued marker survives loss of local invocation inventory.
                MethodInfo invoke =holder.GetType().GetMethod("MapOriginalGuest")!;
                Assert.False((bool)Property(invoke.Invoke(holder,[request])!,"IsSuccess")!);
                object childLease =Property(Property(request,"MappingRequest")!,"ChildLease")!;
                object transition =ReferenceEquals(holder,sdk)?(object)NeutralChildDomainTransition.Start:global::SingPlus.Platform.PlatformChildDomainTransition.Start;
                int transitionCalls =spy.Calls.GetValueOrDefault("TransitionChildDomain");
                Assert.False((bool)Property(holder.GetType().GetMethod("TransitionChildDomain")!.Invoke(holder,[childLease,transition])!,"IsSuccess")!);
                Assert.Equal(transitionCalls,spy.Calls.GetValueOrDefault("TransitionChildDomain"));
                object nestedFactory =Property(childOwner,"NestedFactory")!;
                object nestedRequest =Property(nestedFactory,"Request")!;
                object nextNested=ReferenceEquals(holder,sdk)
                    ? new NeutralOriginalNestedDomainRequest((NeutralChildDomainLease)childLease,((NeutralOriginalNestedDomainRequest)nestedRequest).Intent)
                    : new global::SingPlus.Platform.PlatformOriginalNestedDomainRequest(new((global::SingPlus.Platform.PlatformProviderChildDomainLease)childLease,
                        ((global::SingPlus.Platform.PlatformOriginalNestedDomainRequest)nestedRequest).Request.ChildIntent));
                int nestedCalls =spy.Calls.GetValueOrDefault("CreateOriginalNestedChild");
                Assert.False((bool)Property(holder.GetType().GetMethod("CreateOriginalNestedDomain")!.Invoke(holder,[nextNested])!,"IsSuccess")!);
                Assert.Equal(nestedCalls,spy.Calls.GetValueOrDefault("CreateOriginalNestedChild"));
                Assert.Equal(1,spy.Calls.GetValueOrDefault("MapOriginalGuest"));
                history.Add(fact); // Restore exact fixture inventory, never recreate original authority.
            }
        }
        object? cleanupFact=null, exactKnown=null;
        if(mode=="bridge-known-copy") spy.After=name=>
        {
            if(name!="UnmapOriginalGuest")return;
            object childOwner =((IDictionary)Field(kernel.PlatformAuthority,"_originalChildOwners")).Values.Cast<object>()
                .Single(x=>((IEnumerable)Property(x,"GuestMapInvocations")!).Cast<object>().Any());
            cleanupFact=((IEnumerable)Property(childOwner,"GuestMapInvocations")!).Cast<object>().Single();
            FieldInfo known =cleanupFact.GetType().GetField("KnownMapping")!;
            exactKnown=known.GetValue(cleanupFact)!;
            known.SetValue(cleanupFact,typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(exactKnown,null));
        };
        bool pinned = mode is not ("exact" or "reentry" or "lost-reply" or "bridge-aba" or "no-effect" or "unmap-lost-reply" or "map-and-unmap-loss" or "bridge-known-copy");
        KernelResult terminated = kernel.TerminateProcess(owner);
        if (mode is "unmap-lost-reply" or "map-and-unmap-loss" or "bridge-known-copy")
        {
            Assert.False(terminated.IsSuccess);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.GuestMemoryBytes).Used);
            if(mode=="bridge-known-copy")
            {
                Assert.Equal(1,spy.Calls.GetValueOrDefault("UnmapOriginalGuest"));
                spy.After=null;
                cleanupFact!.GetType().GetField("KnownMapping")!.SetValue(cleanupFact,exactKnown);
            }
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


