using SingPlus.Contracts;
using YAKSys_Hybrid_CPU.Core;
namespace SingPlus.Platform.HybridCpu.Tests;
public sealed class DeviceAdmissionContinuityTests
{
    [Theory]
    [InlineData(0UL)][InlineData(ulong.MaxValue)][InlineData(ulong.MaxValue - 1)]
    public void DeviceCapacityRefusesBeforeNeutralBind(ulong capacity)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var parent = provider.BindDomain(new(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        provider.GetType().GetField("_nextProviderDeviceLeaseId", global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!.SetValue(provider, capacity);
        var result = provider.BindDevice(parent, new("device-capacity"), PlatformDeviceRights.Read);
        Assert.Equal(capacity == ulong.MaxValue - 1, result.IsSuccess);
        Assert.Equal(capacity == ulong.MaxValue - 1 ? 1 : 0, proxy.DeviceBindCalls);
        Assert.Equal(capacity == ulong.MaxValue - 1 ? 1 : 0, proxy.Runtime.ActiveDeviceLeaseCount);
        if (result.IsSuccess)
        {
            Assert.Equal(capacity, result.Value!.LeaseId.Value);
            Assert.True(provider.RevokeDevice(result.Value).IsSuccess);
        }
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
    }
    [Theory]
    [InlineData("exact")][InlineData("owner")][InlineData("unknown")][InlineData("null")]
    [InlineData("throw")][InlineData("handle")][InlineData("epoch")][InlineData("false-denial")]
    public async Task DeviceBindPinsOriginalInvocationAndInterlocksRootClose(string outcome)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var parent = provider.BindDomain(new(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var owners = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains", flags)!.GetValue(provider)!;
        var original = owners[parent.LeaseId]!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        proxy.DeviceBindOutcome = outcome;
        proxy.BeforeDeviceBind = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Device bind timeout.");
            if (outcome == "owner") owners[parent.LeaseId] = Activator.CreateInstance(original.GetType(), parent,
                original.GetType().GetProperty("HybridCpuLease")!.GetValue(original))!;
        };
        var binding = Task.Run(() => provider.BindDevice(parent, new("device-bind"), PlatformDeviceRights.Read));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False((await Task.Run(() => provider.RevokeDomain(parent)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.False(provider.BindDevice(parent, new("device-bind"), PlatformDeviceRights.Read).IsSuccess);
            Assert.Equal(1, proxy.DeviceBindCalls);
            Assert.Equal(0, proxy.CloseCalls);
        }
        finally { release.Set(); }
        PlatformAuthorityResult<PlatformProviderDeviceLease> result = default!;
        if (outcome == "throw") await Assert.ThrowsAsync<IOException>(async () => { await binding; });
        else { result = await binding; Assert.Equal(outcome == "exact", result.IsSuccess); }
        proxy.BeforeDeviceBind = null;
        if (outcome == "owner")
        {
            owners[parent.LeaseId] = original;
            Assert.False(provider.BindDevice(parent, new("different-device"), PlatformDeviceRights.Read).IsSuccess);
            result = provider.BindDevice(parent, new("device-bind"), PlatformDeviceRights.Read);
            Assert.True(result.IsSuccess);
        }
        Assert.Equal(1, proxy.Runtime.ActiveDeviceLeaseCount);
        Assert.Equal(1, proxy.DeviceBindCalls);
        Assert.Equal(0, proxy.DeviceCloseCalls);
        Assert.False(provider.RevokeDomain(parent).IsSuccess);
        if (outcome is "exact" or "owner")
        {
            Assert.True(provider.RevokeDevice(result.Value!).IsSuccess);
            Assert.True(provider.RevokeDomain(parent).IsSuccess);
        }
        else
        {
            Assert.False(provider.BindDevice(parent, new("device-bind"), PlatformDeviceRights.Read).IsSuccess);
            Assert.Equal(1, proxy.DeviceBindCalls);
            Assert.Equal(0, proxy.DeviceCloseCalls);
            Assert.False(provider.RevokeDomain(parent).IsSuccess);
        }
    }
}
