using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class IrqDeliveryContinuityTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PublishedBindingRefusesReplacementSourceBeforeAnyNeutralEffect(bool replaceDevice)
    {
        (HybridCpuPlatformAuthorityProvider? provider, ChildClosureTupleTests.RootCloseRuntimeProxy? proxy, PlatformProviderDomainLease parent, PlatformProviderDeviceLease device, PlatformProviderIrqBinding binding, NeutralInterruptLease neutral) = Create();
        Assert.True(proxy.Runtime.SignalInterrupt(neutral).IsSignaled);
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary owners = (global::System.Collections.IDictionary)provider.GetType()
            .GetField(replaceDevice ? "_deviceLeases" : "_domains", flags)!.GetValue(provider)!;
        object key = replaceDevice ? device.LeaseId : parent.LeaseId;
        object original = owners[key]!;
        owners[key] = ChildClosureTupleTests.ReplacementSourceOwner(original);
        Assert.False(provider.PollInterrupt(binding).IsSuccess);
        Assert.False(provider.CompleteInterruptDelivery(binding, new(1)).IsSuccess);
        Assert.False(provider.RevokeInterrupt(binding).IsSuccess);
        Assert.Equal(0, proxy.IrqPollCalls);
        Assert.Equal(0, proxy.IrqCompleteCalls);
        Assert.Equal(0, proxy.IrqCloseCalls);
        Assert.Equal(1, proxy.Runtime.ActiveInterruptLeaseCount);
        // Restoring the original is test arrangement, not a recovery implementation.
        owners[key] = original;
        PlatformAuthorityResult<PlatformInterruptDeliveryObservation> observed = provider.PollInterrupt(binding);
        Assert.True(observed.IsSuccess);
        Assert.True(provider.CompleteInterruptDelivery(binding, observed.Value!.Sequence).IsSuccess);
        Assert.True(provider.RevokeInterrupt(binding).IsSuccess);
        Assert.Equal(0, proxy.Runtime.ActiveInterruptLeaseCount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AdmittedCloseRefusesConcurrentDeliveryBeforeNeutralEffect(bool pendingDelivery)
    {
        (HybridCpuPlatformAuthorityProvider? provider, ChildClosureTupleTests.RootCloseRuntimeProxy? proxy, PlatformProviderDomainLease parent, PlatformProviderDeviceLease device, PlatformProviderIrqBinding binding, NeutralInterruptLease neutral) = Create();
        if (pendingDelivery) Assert.True(proxy.Runtime.SignalInterrupt(neutral).IsSignaled);
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        proxy.BeforeIrqClose = () =>
        {
            object gate = provider.GetType().GetField("_childCreateCommitGate",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(provider)!;
            Assert.False(Monitor.IsEntered(gate));
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        Task<PlatformAuthorityResult> close = Task.Run(() => provider.RevokeInterrupt(binding));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False((await Task.Run(() => provider.PollInterrupt(binding)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.False(provider.CompleteInterruptDelivery(binding, new(1)).IsSuccess);
            Assert.False(provider.RevokeInterrupt(binding).IsSuccess);
            Assert.False(provider.RevokeDevice(device).IsSuccess);
            Assert.False(provider.RevokeDomain(parent).IsSuccess);
            Assert.Equal(0, proxy.IrqPollCalls);
            Assert.Equal(0, proxy.IrqCompleteCalls);
            Assert.Equal(1, proxy.IrqCloseCalls);
        }
        finally { release.Set(); }
        Assert.Equal(!pendingDelivery, (await close).IsSuccess);
        proxy.BeforeIrqClose = null;
        Assert.Equal(pendingDelivery ? 1 : 0, proxy.Runtime.ActiveInterruptLeaseCount);
        if (pendingDelivery)
        {
            PlatformAuthorityResult<PlatformInterruptDeliveryObservation> observed = provider.PollInterrupt(binding);
            Assert.True(observed.IsSuccess);
            Assert.True(provider.CompleteInterruptDelivery(binding, observed.Value!.Sequence).IsSuccess);
            Assert.True(provider.RevokeInterrupt(binding).IsSuccess);
        }
        else Assert.False(provider.PollInterrupt(binding).IsSuccess);
        Assert.Equal(0, proxy.Runtime.ActiveInterruptLeaseCount);
        Assert.True(provider.RevokeDevice(device).IsSuccess);
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
    }

    private static (HybridCpuPlatformAuthorityProvider Provider, ChildClosureTupleTests.RootCloseRuntimeProxy Proxy,
        PlatformProviderDomainLease Parent, PlatformProviderDeviceLease Device, PlatformProviderIrqBinding Binding,
        NeutralInterruptLease Neutral) Create()
    {
        INeutralDomainRuntime runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        ChildClosureTupleTests.RootCloseRuntimeProxy proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        PlatformProviderDeviceLease device = provider.BindDevice(parent, new("irq-device"), PlatformDeviceRights.Read | PlatformDeviceRights.Configure).Value!;
        PlatformProviderIrqBinding binding = provider.BindInterrupt(device, new("irq", PlatformInterruptTrigger.Edge)).Value!;
        IDictionary records = (global::System.Collections.IDictionary)provider.GetType().GetField("_irqBindings",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(provider)!;
        object record = records[binding.BindingId]!;
        return (provider, proxy, parent, device, binding,
            (NeutralInterruptLease)record.GetType().GetProperty("HybridCpuLease")!.GetValue(record)!);
    }

    [Theory]
    [InlineData(false, "exact")][InlineData(true, "exact")]
    [InlineData(false, "owner")][InlineData(true, "owner")]
    [InlineData(false, "device-owner")][InlineData(true, "device-owner")]
    [InlineData(false, "absent-irq")][InlineData(true, "absent-irq")]
    [InlineData(false, "absent-parent")][InlineData(true, "absent-parent")]
    [InlineData(false, "absent-device")][InlineData(true, "absent-device")]
    [InlineData(false, "unknown")][InlineData(true, "unknown")]
    [InlineData(false, "null")][InlineData(true, "null")]
    [InlineData(false, "throw")][InlineData(true, "throw")]
    [InlineData(false, "lease")][InlineData(false, "sequence")]
    [InlineData(false, "availability")][InlineData(false, "false-decision")]
    [InlineData(true, "revoked")]
    public async Task DeliveryRetainsOriginalTupleAndInterlocksClose(bool complete, string outcome)
    {
        (HybridCpuPlatformAuthorityProvider? provider, ChildClosureTupleTests.RootCloseRuntimeProxy? proxy, PlatformProviderDomainLease parent, PlatformProviderDeviceLease device, PlatformProviderIrqBinding binding, NeutralInterruptLease neutral) = Create();
        NeutralInterruptSignalResult signal = proxy.Runtime.SignalInterrupt(neutral);
        Assert.True(signal.IsSignaled);
        PlatformProviderInterruptDeliverySequence sequence = new PlatformProviderInterruptDeliverySequence(signal.Sequence.Value);
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        string field = outcome is "device-owner" or "absent-device" ? "_deviceLeases" : outcome == "absent-irq" ? "_irqBindings" : "_domains";
        IDictionary owners = (global::System.Collections.IDictionary)provider.GetType().GetField(field, flags)!.GetValue(provider)!;
        object key = field == "_deviceLeases" ? device.LeaseId : field == "_irqBindings" ? binding.BindingId : parent.LeaseId;
        object original = owners[key]!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        Action callback = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("IRQ delivery callback timeout.");
            if (outcome is "owner" or "device-owner") owners[key] = ChildClosureTupleTests.ReplacementSourceOwner(original);
            if (outcome.StartsWith("absent-")) owners.Remove(key);
        };
        if (complete) { proxy.BeforeIrqComplete = callback; proxy.IrqCompleteOutcome = outcome; }
        else { proxy.BeforeIrqPoll = callback; proxy.IrqPollOutcome = outcome; }
        PlatformAuthorityResult Invoke()
        {
            if (complete) return provider.CompleteInterruptDelivery(binding, sequence);
            PlatformAuthorityResult<PlatformInterruptDeliveryObservation> result = provider.PollInterrupt(binding);
            if (result.IsSuccess)
            {
                Assert.True(result.Value!.DeliveryAvailable);
                Assert.Equal(sequence, result.Value.Sequence);
            }
            return result.IsSuccess ? PlatformAuthorityResult.Ok() : PlatformAuthorityResult.Fail(result.Status, result.Message!);
        }
        Task<PlatformAuthorityResult> invocation = Task.Run(Invoke);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False((await Task.Run(() => provider.RevokeInterrupt(binding)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.False(provider.PollInterrupt(binding).IsSuccess);
            Assert.False(provider.CompleteInterruptDelivery(binding, sequence).IsSuccess);
            Assert.False(provider.RevokeDevice(device).IsSuccess);
            Assert.False(provider.RevokeDomain(parent).IsSuccess);
            Assert.Equal(0, proxy.IrqCloseCalls);
            Assert.Equal(complete ? 0 : 1, proxy.IrqPollCalls);
            Assert.Equal(complete ? 1 : 0, proxy.IrqCompleteCalls);
        }
        finally { release.Set(); }
        if (outcome == "throw") await Assert.ThrowsAsync<IOException>(async () => { await invocation; });
        else Assert.Equal(outcome == "exact", (await invocation).IsSuccess);
        proxy.BeforeIrqPoll = null;
        proxy.BeforeIrqComplete = null;
        bool restored = outcome is "owner" or "device-owner" || outcome.StartsWith("absent-");
        if (restored)
        {
            Assert.False(Invoke().IsSuccess);
            owners[key] = original;
        }
        bool known = outcome == "exact" || restored;
        if (complete || outcome != "exact") Assert.Equal(known, Invoke().IsSuccess);
        Assert.Equal(complete ? 0 : 1, proxy.IrqPollCalls);
        Assert.Equal(complete ? 1 : 0, proxy.IrqCompleteCalls);
        if (!complete && known) Assert.True(provider.CompleteInterruptDelivery(binding, sequence).IsSuccess);
        if (!complete && !known)
        {
            Assert.False(provider.RevokeInterrupt(binding).IsSuccess); // Actual pending delivery remains unclosed.
            Assert.Equal(1, proxy.Runtime.ActiveInterruptLeaseCount);
            Assert.False(provider.RevokeDevice(device).IsSuccess);
            Assert.False(provider.RevokeDomain(parent).IsSuccess);
        }
        else
        {
            // Even an unknown completion response is not closure; the separate exact Close consumer is required.
            Assert.Equal(1, proxy.Runtime.ActiveInterruptLeaseCount);
            Assert.False(provider.RevokeDevice(device).IsSuccess);
            Assert.True(provider.RevokeInterrupt(binding).IsSuccess);
            Assert.True(provider.RevokeDevice(device).IsSuccess);
            Assert.True(provider.RevokeDomain(parent).IsSuccess);
        }
    }

    [Fact]
    public void NoDeliveryAndWrongSequenceKeepFreshDeliveryUsable()
    {
        (HybridCpuPlatformAuthorityProvider? provider, ChildClosureTupleTests.RootCloseRuntimeProxy? proxy, PlatformProviderDomainLease parent, PlatformProviderDeviceLease device, PlatformProviderIrqBinding binding, NeutralInterruptLease neutral) = Create();
        Assert.False(provider.PollInterrupt(binding).IsSuccess); // Preserve existing NoDelivery result.
        Assert.False(provider.CompleteInterruptDelivery(binding, default).IsSuccess);
        Assert.Equal(0, proxy.IrqCompleteCalls);
        NeutralInterruptSignalResult signal = proxy.Runtime.SignalInterrupt(neutral);
        PlatformProviderInterruptDeliverySequence sequence = new PlatformProviderInterruptDeliverySequence(signal.Sequence.Value);
        Assert.Equal(PlatformAuthorityStatus.Stale, provider.CompleteInterruptDelivery(binding, new(sequence.Value + 1)).Status);
        Assert.True(provider.PollInterrupt(binding).IsSuccess);
        Assert.True(provider.CompleteInterruptDelivery(binding, sequence).IsSuccess);
        Assert.True(provider.CompleteInterruptDelivery(binding, sequence).IsSuccess);
        Assert.Equal(2, proxy.IrqCompleteCalls); // Wrong sequence + one actual completion, no duplicate effect.
        Assert.True(proxy.Runtime.SignalInterrupt(neutral).IsSignaled);
        PlatformAuthorityResult<PlatformInterruptDeliveryObservation> next = provider.PollInterrupt(binding);
        Assert.True(next.IsSuccess);
        Assert.NotEqual(sequence, next.Value!.Sequence);
        Assert.True(provider.CompleteInterruptDelivery(binding, next.Value.Sequence).IsSuccess);
        Assert.Equal(3, proxy.IrqCompleteCalls);
        Assert.True(provider.RevokeInterrupt(binding).IsSuccess);
        Assert.True(provider.RevokeDevice(device).IsSuccess);
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
    }
}
