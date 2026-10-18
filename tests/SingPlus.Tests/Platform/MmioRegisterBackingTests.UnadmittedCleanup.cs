using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed partial class MmioRegisterBackingTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AdmittedMapOwnerLossCannotBecomeLocalNoEffectRollback(bool copy)
    {
        var proxy = DispatchProxy.Create<IRuntime, Spy>(); var spy = (Spy)(object)proxy;
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(proxy));
        var owner = TestFixtures.Create(kernel, 3095, 30950).Handle;
        var admin = TestFixtures.Create(kernel, 3096, 30960).Handle;
        var cap = kernel.MintCapability(new(30960), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, cap, owner, "admitted-mmio",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).Value!.ProcessBudget;
        var buffer = kernel.AllocateBuffer<byte>(owner, 64).Value!;
        var device = Device(kernel, owner, new(30950), "device/admitted");
        var capability = kernel.MintCapability(new(30950), owner, ResourceKind.MmioRegion,
            CapabilityResourceIds.MmioRegion("device/admitted", "bank", 64),
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        object payload = null!, original = null!; PropertyInfo route = null!;
        spy.AfterMap = () =>
        {
            var regions = (IDictionary)typeof(RegionAuthority).GetField("_regions", flags)!.GetValue(kernel.Regions)!;
            var region = regions[buffer.Handle.RegionId]!;
            var uses = (IDictionary)region.GetType().GetProperty("Uses")!.GetValue(region)!;
            var use = Assert.Single(uses.Values.Cast<object>());
            payload = use.GetType().GetProperty("ReleaseOwnerMmio")!.GetValue(use)!;
            route = payload.GetType().GetProperty("AdmissionOwner", flags)!;
            original = route.GetValue(payload)!;
            route.SetValue(payload, copy ? typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null) : null);
        };
        var bound = kernel.BindPlatformMmioBacking(owner, device, capability, 0, 64,
            PlatformMmioAccess.Read | PlatformMmioAccess.Write, buffer);
        Assert.False(bound.IsSuccess);
        Assert.Equal(1, spy.Maps); Assert.Equal(0, spy.Closes);
        Assert.NotEqual(RegionUseState.Released, Assert.Single(kernel.Regions.SnapshotUses()).State);
        Assert.Throws<InvalidOperationException>(() => buffer.Span.ToArray());
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(0, spy.Closes);
        Assert.Equal(64UL, kernel.QueryBudget(budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        route.SetValue(payload, original);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, spy.Maps); Assert.Equal(1, spy.Closes);
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task UnadmittedBackingRollbackRetainsOriginalUseUntilByteReservationEnds(bool foreignRoute, bool directRelease)
    {
        var proxy = DispatchProxy.Create<IRuntime, Spy>(); var spy = (Spy)(object)proxy;
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(proxy));
        var owner = TestFixtures.Create(kernel, 3091, 30910).Handle;
        var priorState = kernel.Processes.Resolve(owner).Value!.State;
        var foreign = TestFixtures.Create(kernel, 3092, 30920).Handle;
        var admin = TestFixtures.Create(kernel, 3093, 30930).Handle;
        var budgetCap = kernel.MintCapability(new(30930), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "unadmitted-mmio",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).Value!.ProcessBudget;
        var foreignBudget = kernel.AdmitProcessBudget(admin, budgetCap, foreign, "foreign-mmio",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 32)]).Value!.ProcessBudget;
        var buffer = kernel.AllocateBuffer<byte>(owner, 64).Value!;
        buffer.Span.Fill(0x35);
        var peer = kernel.AllocateBuffer<byte>(foreign, 32).Value!;
        var device = Device(kernel, owner, new(30910), "device/unadmitted");
        var foreignDevice = Device(kernel, foreign, new(30920), "device/unadmitted-peer");
        CapabilityId Mint(ProcessHandle process, ulong domain, string name, long size) => kernel.MintCapability(new(domain), process,
            ResourceKind.MmioRegion, CapabilityResourceIds.MmioRegion(name, "bank", size),
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var capability = Mint(owner, 30910, "device/unadmitted", 64);
        var foreignMap = kernel.BindPlatformMmioBacking(foreign, foreignDevice,
            Mint(foreign, 30920, "device/unadmitted-peer", 32), 0, 32,
            PlatformMmioAccess.Read | PlatformMmioAccess.Write, peer).Value;
        Assert.True(kernel.AccessPlatformMmio32(foreign, new(foreignMap, 0, true, 99)).IsSuccess);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        object Property(object value, string name) => value.GetType().GetProperty(name, flags)!.GetValue(value)!;
        object Payload(OwnedBuffer<byte> bytes)
        {
            var regions = (IDictionary)typeof(RegionAuthority).GetField("_regions", flags)!.GetValue(kernel.Regions)!;
            var uses = (IDictionary)Property(regions[bytes.Handle.RegionId]!, "Uses");
            return Property(Assert.Single(uses.Values.Cast<object>()), "ReleaseOwnerMmio");
        }
        var other = Payload(peer);
        var foreignLease = Property(other, "Reservation");
        var gate = typeof(CapabilityAuthority).GetField("_gate", flags)!.GetValue(kernel.CapabilityAuthority)!;
        Task<KernelResult<PlatformMmioLease>> bind = null!;
        object payload = null!, storage = null!; FieldInfo route = null!; object? original = null;
        Monitor.Enter(gate);
        try
        {
            bind = Task.Run(() => kernel.BindPlatformMmioBacking(owner, device, capability, 0, 64,
                PlatformMmioAccess.Read | PlatformMmioAccess.Write, buffer));
            Assert.True(SpinWait.SpinUntil(() => kernel.Regions.SnapshotUses().Any(u => u.Region == buffer.Handle), TimeSpan.FromSeconds(10)));
            payload = Payload(buffer);
            Assert.True(SpinWait.SpinUntil(() => payload.GetType().GetProperty("Backing", flags)!.GetValue(payload) is not null, TimeSpan.FromSeconds(10)));
            // The worker is stopped by the actual authority gate, before the first provider owner/effect.
            var lease = Property(payload, "Reservation");
            storage = lease.GetType().GetField("_storage", flags)!.GetValue(lease)!;
            route = storage.GetType().GetField("_runtimeReservation", flags)!;
            original = route.GetValue(storage);
            route.SetValue(storage, foreignRoute ? foreignLease.GetType().GetField("_lifetime", flags)!.GetValue(foreignLease) : null);
            Assert.Null(payload.GetType().GetProperty("AdmissionOwner", flags)!.GetValue(payload));
            Assert.True(kernel.CapabilityAuthority.Revoke(capability).IsSuccess);
        }
        finally { Monitor.Exit(gate); }
        KernelResult<PlatformMmioLease> result = default;
        var escaped = await Record.ExceptionAsync(async () => result = await bind.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(escaped); Assert.False(result.IsSuccess);
        Assert.Equal(1, spy.Maps); Assert.Equal(0, spy.Closes); // Only the independent foreign Map exists.
        Assert.NotEqual(RegionUseState.Released, kernel.Regions.SnapshotUses().Single(u => u.Region == buffer.Handle).State);
        Assert.False(kernel.ReleaseRegion(owner, buffer).IsSuccess);
        if (!directRelease) Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        ulong Used(BudgetAccountHandle account) => kernel.QueryBudget(account).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used;
        Assert.Equal(64UL, Used(budget)); Assert.Equal(32UL, Used(foreignBudget));
        Assert.True((bool)Property(foreignLease, "IsActive"));
        route.SetValue(storage, original);
        if (directRelease)
        {
            var copy = (OwnedBuffer<byte>)typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(buffer, null)!;
            Assert.False(kernel.ReleaseRegion(owner, copy).IsSuccess);
            Assert.NotEqual(RegionUseState.Released, kernel.Regions.SnapshotUses().Single(u => u.Region == buffer.Handle).State);
            Assert.Equal(64UL, Used(budget));
        }
        var ended = directRelease ? kernel.ReleaseRegion(owner, buffer) : kernel.TerminateProcess(owner);
        Assert.True(ended.IsSuccess, ended.Message);
        if (directRelease)
        {
            Assert.Equal(priorState, kernel.Processes.Resolve(owner).Value!.State);
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        }
        Assert.Equal(0UL, Used(budget)); Assert.Equal(32UL, Used(foreignBudget));
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == buffer.Handle).State);
        Assert.Equal(RegionUseState.Released, kernel.Regions.SnapshotUses().Single(u => u.Region == buffer.Handle).State);
        Assert.Equal(1, spy.Maps); Assert.Equal(0, spy.Closes);
        var read = kernel.AccessPlatformMmio32(foreign, new(foreignMap, 0, false));
        Assert.True(read.IsSuccess, read.Message); Assert.Equal(99U, read.Value);
        Assert.True((bool)Property(foreignLease, "IsActive"));
    }
}
