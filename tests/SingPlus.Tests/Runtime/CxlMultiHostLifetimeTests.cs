using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class CxlMultiHostLifetimeTests
{
    [Fact]
    public void ProcessTeardownRetainsOriginalUseRegionAndChargeAndLeavesForeignBindingLive()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle handle = TestFixtures.Create(kernel, 2501, 2502).Handle;
        ProcessHandle foreign = TestFixtures.Create(kernel, 2503, 2504).Handle;
        ProcessHandle admin = TestFixtures.Create(kernel, 2505, 2506).Handle;
        CapabilityId cap = kernel.MintCapability(new(2506), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(admin, cap, handle, "multi-host-retained",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 32)]).Value!.ProcessBudget;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(handle, 32).Value!;
        OwnedBuffer<byte> other = kernel.AllocateBuffer<byte>(foreign, 32).Value!;
        RegionOwner owner = new RegionOwner(new(2502), handle.Generation);
        CxlMultiHostGate gate = new CxlMultiHostGate(kernel);
        MultiHostMemoryBinding original = gate.Bind(buffer.Handle, owner, new("original"), MultiHostAccessMode.Writable, false).Value!;
        MultiHostMemoryBinding independent = gate.Bind(other.Handle, new(new(2504), foreign.Generation), new("foreign"), MultiHostAccessMode.Writable, false).Value!;
        RegionUseDescriptor use = kernel.Regions.SnapshotUses().Single(u => u.Region.RegionId == buffer.Handle.RegionId);

        Assert.False(kernel.TerminateProcess(handle).IsSuccess);
        Assert.False(kernel.TerminateProcess(handle).IsSuccess);
        Assert.Equal(MultiHostBindingState.Fenced, gate.Query(original.Binding).Value!.State);
        Assert.Equal(MultiHostBindingState.Active, gate.Query(independent.Binding).Value!.State);
        Assert.False(gate.Bind(buffer.Handle, owner, new("replacement"), MultiHostAccessMode.Writable, false).IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, gate.BeginReclaim(original.Binding).Error);
        Assert.Equal(KernelError.PlatformUnsupported, gate.CompleteReclaim(original.Binding).Error);
        RegionUseDescriptor retained = kernel.Regions.SnapshotUses().Single(u => u.Handle == use.Handle);
        Assert.NotEqual(RegionUseState.Released, retained.State);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == buffer.Handle).State);
        Assert.Equal(32UL, kernel.QueryBudget(budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == other.Handle).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentAdmissionPublishesUniqueUsesAndNeverTwoWriters(bool readers)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle handle = TestFixtures.Create(kernel, 2511, 2512).Handle;
        RegionOwner owner = new RegionOwner(new(2512), handle.Generation);
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(handle, 32).Value!;
        CxlMultiHostGate gate = new CxlMultiHostGate(kernel);
        using ManualResetEventSlim start = new ManualResetEventSlim();
        Task<KernelResult<MultiHostMemoryBinding>>[] attempts = Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            start.Wait();
            return gate.Bind(buffer.Handle, owner, new($"host-{i}"), readers ? MultiHostAccessMode.ReadOnly : MultiHostAccessMode.Writable, readers);
        })).ToArray();
        start.Set();
        KernelResult<MultiHostMemoryBinding>[] results = await Task.WhenAll(attempts);
        MultiHostMemoryBinding[] admitted = results.Where(r => r.IsSuccess).Select(r => r.Value!).ToArray();
        Assert.Equal(readers ? 16 : 1, admitted.Length);
        Assert.Equal(admitted.Length, admitted.Select(b => b.Binding.BindingId).Distinct().Count());
        Assert.Equal(admitted.Length, kernel.Regions.SnapshotUses().Count(u => u.State == RegionUseState.Active));
        if (!readers) Assert.All(results.Where(r => !r.IsSuccess), r => Assert.Equal(KernelError.RegionUseConflict, r.Error));
    }

    [Fact]
    public void BindingIdentityExhaustionRefusesBeforeAnotherRegionUse()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle handle = TestFixtures.Create(kernel, 2521, 2522).Handle;
        RegionOwner owner = new RegionOwner(new(2522), handle.Generation);
        OwnedBuffer<byte> first = kernel.AllocateBuffer<byte>(handle, 16).Value!;
        OwnedBuffer<byte> second = kernel.AllocateBuffer<byte>(handle, 16).Value!;
        CxlMultiHostGate gate = new CxlMultiHostGate(kernel);
        typeof(CxlMultiHostGate).GetField("_nextId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(gate, ulong.MaxValue);
        Assert.True(gate.Bind(first.Handle, owner, new("last"), MultiHostAccessMode.Writable, false).IsSuccess);
        Assert.Equal(KernelError.CapacityExhausted, gate.Bind(second.Handle, owner, new("overflow"), MultiHostAccessMode.Writable, false).Error);
        Assert.Single(kernel.Regions.SnapshotUses());
    }
}
