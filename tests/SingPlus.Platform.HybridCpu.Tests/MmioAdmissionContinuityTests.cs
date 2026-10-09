using SingPlus.Contracts;
using YAKSys_Hybrid_CPU.Core;
namespace SingPlus.Platform.HybridCpu.Tests;
public sealed class MmioAdmissionContinuityTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void PublishedMappingRefusesLostOriginalSourceBeforeNeutralClose(bool deviceSource, bool absent)
    {
        var (provider, proxy, parent, device) = Create();
        var lease = provider.MapMmio(device, new("mmio", 4096), new(0, 64), PlatformMmioAccess.Read).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var owners = (global::System.Collections.IDictionary)provider.GetType()
            .GetField(deviceSource ? "_deviceLeases" : "_domains", flags)!.GetValue(provider)!;
        object key = deviceSource ? device.LeaseId : parent.LeaseId;
        var original = owners[key]!;
        if (absent) owners.Remove(key);
        else owners[key] = Activator.CreateInstance(original.GetType(),
            original.GetType().GetProperty("Lease")!.GetValue(original),
            original.GetType().GetProperty("HybridCpuLease")!.GetValue(original))!;
        Assert.False(provider.RevokeMmio(lease).IsSuccess);
        Assert.False(provider.RevokeMmio(lease).IsSuccess);
        Assert.False(provider.RevokeDevice(device).IsSuccess);
        Assert.Equal(0, proxy.MmioCloseCalls);
        Assert.Equal(0, proxy.DeviceCloseCalls);
        Assert.Equal(1, proxy.Runtime.ActiveMmioLeaseCount);
        // Restoring the source only arranges the positive control; it is not durable recovery.
        owners[key] = original;
        Assert.True(provider.RevokeMmio(lease).IsSuccess);
        Assert.Equal(1, proxy.MmioCloseCalls);
        Assert.Equal(0, proxy.Runtime.ActiveMmioLeaseCount);
        Assert.True(provider.RevokeDevice(device).IsSuccess);
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
    }

    private static (HybridCpuPlatformAuthorityProvider Provider, ChildClosureTupleTests.RootCloseRuntimeProxy Proxy,
        PlatformProviderDomainLease Parent, PlatformProviderDeviceLease Device) Create()
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var parent = provider.BindDomain(new(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        var device = provider.BindDevice(parent, new("mmio-device"), PlatformDeviceRights.Read).Value!;
        return (provider, proxy, parent, device);
    }
    [Theory]
    [InlineData("exact")][InlineData("owner")][InlineData("device-owner")]
    [InlineData("unknown")][InlineData("null")][InlineData("throw")][InlineData("revoked")][InlineData("not-found")]
    [InlineData("absent-mmio")][InlineData("absent-device")][InlineData("absent-parent")]
    public async Task MmioCloseRetainsOriginalOutcomeBeforeSourceRelease(string outcome)
    {
        var (provider, proxy, parent, device) = Create();
        var lease = provider.MapMmio(device, new("mmio", 4096), new(0, 64), PlatformMmioAccess.Read).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var field = outcome is "device-owner" or "absent-device" ? "_deviceLeases" :
            outcome == "absent-mmio" ? "_mmioLeases" : "_domains";
        var owners = (global::System.Collections.IDictionary)provider.GetType().GetField(field, flags)!.GetValue(provider)!;
        object key = field == "_deviceLeases" ? device.LeaseId : field == "_mmioLeases" ? lease.LeaseId : parent.LeaseId;
        var original = owners[key]!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        proxy.MmioCloseOutcome = outcome;
        proxy.BeforeMmioClose = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("MMIO close timeout.");
            if (outcome is "owner" or "device-owner") owners[key] = Activator.CreateInstance(original.GetType(),
                original.GetType().GetProperty("Lease")!.GetValue(original), original.GetType().GetProperty("HybridCpuLease")!.GetValue(original))!;
            if (outcome.StartsWith("absent-")) owners.Remove(key);
        };
        var closing = Task.Run(() => provider.RevokeMmio(lease));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False((await Task.Run(() => provider.RevokeMmio(lease)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.False(provider.RevokeDevice(device).IsSuccess);
            Assert.False(provider.RevokeDomain(parent).IsSuccess);
            Assert.Equal(1, proxy.MmioCloseCalls);
            Assert.Equal(0, proxy.DeviceCloseCalls);
        }
        finally { release.Set(); }
        if (outcome == "throw") await Assert.ThrowsAsync<IOException>(async () => { await closing; });
        else Assert.Equal(outcome == "exact", (await closing).IsSuccess);
        Assert.Equal(0, proxy.Runtime.ActiveMmioLeaseCount);
        proxy.BeforeMmioClose = null;
        var restored = outcome is "owner" or "device-owner" || outcome.StartsWith("absent-");
        if (restored)
        {
            Assert.False(provider.RevokeMmio(lease).IsSuccess);
            owners[key] = original;
        }
        Assert.Equal(restored, provider.RevokeMmio(lease).IsSuccess);
        Assert.Equal(1, proxy.MmioCloseCalls);
        var closed = restored || outcome == "exact";
        Assert.Equal(closed, provider.RevokeDevice(device).IsSuccess);
        Assert.Equal(closed, provider.RevokeDomain(parent).IsSuccess);
    }
    [Theory]
    [InlineData(0UL)][InlineData(ulong.MaxValue)][InlineData(ulong.MaxValue - 1)]
    public void MmioCapacityRefusesBeforeNeutralEffect(ulong capacity)
    {
        var (provider, proxy, parent, device) = Create();
        provider.GetType().GetField("_nextProviderMmioLeaseId", global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!.SetValue(provider, capacity);
        var result = provider.MapMmio(device, new("mmio", 4096), new(0, 64), PlatformMmioAccess.Read);
        Assert.Equal(capacity == ulong.MaxValue - 1, result.IsSuccess);
        Assert.Equal(capacity == ulong.MaxValue - 1 ? 1 : 0, proxy.MmioMapCalls);
        Assert.Equal(capacity == ulong.MaxValue - 1 ? 1 : 0, proxy.Runtime.ActiveMmioLeaseCount);
        if (result.IsSuccess)
        {
            Assert.Equal(capacity, result.Value!.LeaseId.Value);
            Assert.True(provider.RevokeMmio(result.Value).IsSuccess);
        }
        Assert.True(provider.RevokeDevice(device).IsSuccess);
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
    }
    [Theory]
    [InlineData("exact")][InlineData("owner")][InlineData("device-owner")][InlineData("unknown")]
    [InlineData("null")][InlineData("throw")][InlineData("handle")][InlineData("epoch")][InlineData("false-denial")]
    public async Task MmioPinsOriginalSourcesAndInterlocksDeviceClose(string outcome)
    {
        var (provider, proxy, parent, device) = Create();
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var owners = (global::System.Collections.IDictionary)provider.GetType().GetField(
            outcome == "device-owner" ? "_deviceLeases" : "_domains", flags)!.GetValue(provider)!;
        object key = outcome == "device-owner" ? device.LeaseId : parent.LeaseId;
        var original = owners[key]!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        proxy.MmioMapOutcome = outcome;
        proxy.BeforeMmioMap = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("MMIO map timeout.");
            if (outcome is "owner" or "device-owner") owners[key] = Activator.CreateInstance(original.GetType(),
                original.GetType().GetProperty("Lease")!.GetValue(original), original.GetType().GetProperty("HybridCpuLease")!.GetValue(original))!;
        };
        PlatformAuthorityResult<PlatformProviderMmioLease> Map() => provider.MapMmio(device, new("mmio", 4096), new(0, 64), PlatformMmioAccess.Read);
        var mapping = Task.Run(Map);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False((await Task.Run(() => provider.RevokeDevice(device)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.False(provider.RevokeDomain(parent).IsSuccess);
            Assert.False(Map().IsSuccess);
            Assert.Equal(1, proxy.MmioMapCalls);
            Assert.Equal(0, proxy.MmioCloseCalls);
            Assert.Equal(0, proxy.DeviceCloseCalls);
        }
        finally { release.Set(); }
        PlatformAuthorityResult<PlatformProviderMmioLease> result = default!;
        if (outcome == "throw") await Assert.ThrowsAsync<IOException>(async () => { await mapping; });
        else { result = await mapping; Assert.Equal(outcome == "exact", result.IsSuccess); }
        proxy.BeforeMmioMap = null;
        if (outcome is "owner" or "device-owner")
        {
            owners[key] = original;
            Assert.False(provider.MapMmio(device, new("different-mmio", 4096), new(0, 64), PlatformMmioAccess.Read).IsSuccess);
            result = Map();
            Assert.True(result.IsSuccess);
        }
        Assert.Equal(1, proxy.Runtime.ActiveMmioLeaseCount);
        Assert.Equal(1, proxy.MmioMapCalls);
        Assert.Equal(0, proxy.MmioCloseCalls);
        Assert.False(provider.RevokeDevice(device).IsSuccess);
        if (outcome is "exact" or "owner" or "device-owner")
        {
            Assert.True(provider.RevokeMmio(result.Value!).IsSuccess);
            Assert.True(provider.RevokeDevice(device).IsSuccess);
            Assert.True(provider.RevokeDomain(parent).IsSuccess);
        }
        else
        {
            Assert.False(Map().IsSuccess);
            Assert.Equal(1, proxy.MmioMapCalls);
            Assert.Equal(0, proxy.MmioCloseCalls);
            Assert.False(provider.RevokeDevice(device).IsSuccess);
            Assert.False(provider.RevokeDomain(parent).IsSuccess);
        }
    }
}
