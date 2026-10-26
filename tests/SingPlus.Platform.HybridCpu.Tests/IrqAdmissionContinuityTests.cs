using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using YAKSys_Hybrid_CPU.Core;
namespace SingPlus.Platform.HybridCpu.Tests;
public sealed class IrqAdmissionContinuityTests
{
    private static (HybridCpuPlatformAuthorityProvider Provider, ChildClosureTupleTests.RootCloseRuntimeProxy Proxy,
        PlatformProviderDomainLease Parent, PlatformProviderDeviceLease Device) Create()
    {
        INeutralDomainRuntime runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        ChildClosureTupleTests.RootCloseRuntimeProxy proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        PlatformProviderDeviceLease device = provider.BindDevice(parent, new("mmio-device"), (PlatformDeviceRights.Read | PlatformDeviceRights.Configure)).Value!;
        return (provider, proxy, parent, device);
    }
    [Theory]
    [InlineData("exact")][InlineData("owner")][InlineData("device-owner")]
    [InlineData("unknown")][InlineData("null")][InlineData("throw")][InlineData("revoked")][InlineData("not-found")]
    [InlineData("absent-irq")][InlineData("absent-device")][InlineData("absent-parent")]
    public async Task IrqCloseRetainsOriginalOutcomeBeforeSourceRelease(string outcome)
    {
        (HybridCpuPlatformAuthorityProvider? provider, ChildClosureTupleTests.RootCloseRuntimeProxy? proxy, PlatformProviderDomainLease parent, PlatformProviderDeviceLease device) = Create();
        PlatformProviderIrqBinding lease = provider.BindInterrupt(device, new("irq", PlatformInterruptTrigger.Edge)).Value!;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        string field = outcome is "device-owner" or "absent-device" ? "_deviceLeases" :
            outcome == "absent-irq" ? "_irqBindings" : "_domains";
        IDictionary owners = (global::System.Collections.IDictionary)provider.GetType().GetField(field, flags)!.GetValue(provider)!;
        object key = field == "_deviceLeases" ? device.LeaseId : field == "_irqBindings" ? lease.BindingId : parent.LeaseId;
        object original = owners[key]!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        proxy.IrqCloseOutcome = outcome;
        proxy.BeforeIrqClose = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("IRQ close timeout.");
            if (outcome is "owner" or "device-owner") owners[key] = ChildClosureTupleTests.ReplacementSourceOwner(original);
            if (outcome.StartsWith("absent-")) owners.Remove(key);
        };
        Task<PlatformAuthorityResult> closing = Task.Run(() => provider.RevokeInterrupt(lease));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False((await Task.Run(() => provider.RevokeInterrupt(lease)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.False(provider.RevokeDevice(device).IsSuccess);
            Assert.False(provider.RevokeDomain(parent).IsSuccess);
            Assert.False(provider.PollInterrupt(lease).IsSuccess);
            Assert.Equal(0, proxy.IrqPollCalls);
            Assert.Equal(1, proxy.IrqCloseCalls);
            Assert.Equal(0, proxy.DeviceCloseCalls);
        }
        finally { release.Set(); }
        if (outcome == "throw") await Assert.ThrowsAsync<IOException>(async () => { await closing; });
        else Assert.Equal(outcome == "exact", (await closing).IsSuccess);
        Assert.Equal(0, proxy.Runtime.ActiveInterruptLeaseCount);
        proxy.BeforeIrqClose = null;
        bool restored = outcome is "owner" or "device-owner" || outcome.StartsWith("absent-");
        if (restored)
        {
            Assert.False(provider.RevokeInterrupt(lease).IsSuccess);
            owners[key] = original;
        }
        Assert.Equal(restored, provider.RevokeInterrupt(lease).IsSuccess);
        Assert.Equal(1, proxy.IrqCloseCalls);
        bool closed = restored || outcome == "exact";
        Assert.Equal(closed, provider.RevokeDevice(device).IsSuccess);
        Assert.Equal(closed, provider.RevokeDomain(parent).IsSuccess);
    }
    [Fact]
    public void PendingDeliveryAllowsDrainThenFreshClose()
    {
        (HybridCpuPlatformAuthorityProvider? provider, ChildClosureTupleTests.RootCloseRuntimeProxy? proxy, PlatformProviderDomainLease parent, PlatformProviderDeviceLease device) = Create();
        PlatformProviderIrqBinding binding = provider.BindInterrupt(device, new("irq", PlatformInterruptTrigger.Edge)).Value!;
        IDictionary records = (global::System.Collections.IDictionary)provider.GetType().GetField("_irqBindings",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(provider)!;
        object record = records[binding.BindingId]!;
        NeutralInterruptLease neutral = (NeutralInterruptLease)record.GetType().GetProperty("HybridCpuLease")!.GetValue(record)!;
        Assert.True(proxy.Runtime.SignalInterrupt(neutral).IsSignaled);
        Assert.False(provider.RevokeInterrupt(binding).IsSuccess);
        Assert.Equal(1, proxy.IrqCloseCalls);
        Assert.Equal(1, proxy.Runtime.ActiveInterruptLeaseCount);
        PlatformAuthorityResult<PlatformInterruptDeliveryObservation> delivery = provider.PollInterrupt(binding);
        Assert.True(delivery.IsSuccess);
        Assert.True(delivery.Value!.DeliveryAvailable);
        Assert.True(provider.CompleteInterruptDelivery(binding, delivery.Value.Sequence).IsSuccess);
        Assert.True(provider.RevokeInterrupt(binding).IsSuccess);
        Assert.Equal(2, proxy.IrqCloseCalls);
        Assert.Equal(0, proxy.Runtime.ActiveInterruptLeaseCount);
        Assert.True(provider.RevokeDevice(device).IsSuccess);
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
    }
    [Theory]
    [InlineData(0UL)][InlineData(ulong.MaxValue)][InlineData(ulong.MaxValue - 1)]
    public void IrqCapacityRefusesBeforeNeutralEffect(ulong capacity)
    {
        (HybridCpuPlatformAuthorityProvider? provider, ChildClosureTupleTests.RootCloseRuntimeProxy? proxy, PlatformProviderDomainLease parent, PlatformProviderDeviceLease device) = Create();
        provider.GetType().GetField("_nextProviderIrqBindingId", global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!.SetValue(provider, capacity);
        PlatformAuthorityResult<PlatformProviderIrqBinding> result = provider.BindInterrupt(device, new("irq", PlatformInterruptTrigger.Edge));
        Assert.Equal(capacity == ulong.MaxValue - 1, result.IsSuccess);
        Assert.Equal(capacity == ulong.MaxValue - 1 ? 1 : 0, proxy.IrqBindCalls);
        Assert.Equal(capacity == ulong.MaxValue - 1 ? 1 : 0, proxy.Runtime.ActiveInterruptLeaseCount);
        if (result.IsSuccess)
        {
            Assert.Equal(capacity, result.Value!.BindingId.Value);
            Assert.True(provider.RevokeInterrupt(result.Value).IsSuccess);
        }
        Assert.True(provider.RevokeDevice(device).IsSuccess);
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
    }
    [Theory]
    [InlineData("exact")][InlineData("owner")][InlineData("device-owner")][InlineData("unknown")]
    [InlineData("null")][InlineData("throw")][InlineData("handle")][InlineData("epoch")][InlineData("false-denial")]
    public async Task IrqPinsOriginalSourcesAndInterlocksDeviceClose(string outcome)
    {
        (HybridCpuPlatformAuthorityProvider? provider, ChildClosureTupleTests.RootCloseRuntimeProxy? proxy, PlatformProviderDomainLease parent, PlatformProviderDeviceLease device) = Create();
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary owners = (global::System.Collections.IDictionary)provider.GetType().GetField(
            outcome == "device-owner" ? "_deviceLeases" : "_domains", flags)!.GetValue(provider)!;
        object key = outcome == "device-owner" ? device.LeaseId : parent.LeaseId;
        object original = owners[key]!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        proxy.IrqBindOutcome = outcome;
        proxy.BeforeIrqBind = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("IRQ map timeout.");
            if (outcome is "owner" or "device-owner") owners[key] = ChildClosureTupleTests.ReplacementSourceOwner(original);
        };
        PlatformAuthorityResult<PlatformProviderIrqBinding> Map() => provider.BindInterrupt(device, new("irq", PlatformInterruptTrigger.Edge));
        Task<PlatformAuthorityResult<PlatformProviderIrqBinding>> mapping = Task.Run(Map);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False((await Task.Run(() => provider.RevokeDevice(device)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.False(provider.RevokeDomain(parent).IsSuccess);
            Assert.False(Map().IsSuccess);
            Assert.Equal(1, proxy.IrqBindCalls);
            Assert.Equal(0, proxy.IrqCloseCalls);
            Assert.Equal(0, proxy.DeviceCloseCalls);
        }
        finally { release.Set(); }
        PlatformAuthorityResult<PlatformProviderIrqBinding> result = default!;
        if (outcome == "throw") await Assert.ThrowsAsync<IOException>(async () => { await mapping; });
        else { result = await mapping; Assert.Equal(outcome == "exact", result.IsSuccess); }
        proxy.BeforeIrqBind = null;
        if (outcome is "owner" or "device-owner")
        {
            owners[key] = original;
            Assert.False(provider.BindInterrupt(device, new("different-irq", PlatformInterruptTrigger.Edge)).IsSuccess);
            result = Map();
            Assert.True(result.IsSuccess);
        }
        Assert.Equal(1, proxy.Runtime.ActiveInterruptLeaseCount);
        Assert.Equal(1, proxy.IrqBindCalls);
        Assert.Equal(0, proxy.IrqCloseCalls);
        Assert.False(provider.RevokeDevice(device).IsSuccess);
        if (outcome is "exact" or "owner" or "device-owner")
        {
            Assert.True(provider.RevokeInterrupt(result.Value!).IsSuccess);
            Assert.True(provider.RevokeDevice(device).IsSuccess);
            Assert.True(provider.RevokeDomain(parent).IsSuccess);
        }
        else
        {
            Assert.False(Map().IsSuccess);
            Assert.Equal(1, proxy.IrqBindCalls);
            Assert.Equal(0, proxy.IrqCloseCalls);
            Assert.False(provider.RevokeDevice(device).IsSuccess);
            Assert.False(provider.RevokeDomain(parent).IsSuccess);
        }
    }
}
