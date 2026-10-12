using SingPlus.Contracts;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class RootBindContinuityTests
{
    [Theory]
    [InlineData(0UL)][InlineData(ulong.MaxValue)][InlineData(ulong.MaxValue - 1)]
    public void CapacityRefusesBeforeNeutralBind(ulong capacity)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        provider.GetType().GetField("_nextProviderDomainId", global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!.SetValue(provider, capacity);
        var result = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7)));
        var usable = capacity == ulong.MaxValue - 1;
        Assert.Equal(usable, result.IsSuccess);
        Assert.Equal(usable ? 1 : 0, proxy.RootBindCalls);
        Assert.Equal(usable ? 1 : 0, proxy.Runtime.ActiveBindingCount);
        if (usable)
        {
            Assert.Equal(capacity, result.Value!.LeaseId.Value);
            Assert.True(provider.RevokeDomain(result.Value).IsSuccess);
            Assert.False(provider.BindDomain(new PlatformDomainIdentity(new DomainId(11), new ProcessHandle(new ProcessId(13), 7))).IsSuccess);
            Assert.Equal(1, proxy.RootBindCalls);
        }
    }

    [Theory]
    [InlineData("exact")][InlineData("unknown")][InlineData("false-success")][InlineData("epoch")][InlineData("throw")]
    public async Task SameSubjectCannotRepeatPendingOrUnknownOriginalBind(string outcome)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var subject = new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        proxy.RootBindOutcome = outcome;
        proxy.BeforeRootBind = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); };
        var binding = Task.Run(() => provider.BindDomain(subject));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False((await Task.Run(() => provider.BindDomain(subject)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.Equal(1, proxy.RootBindCalls);
        }
        finally { release.Set(); }
        if (outcome == "throw") await Assert.ThrowsAsync<IOException>(async () => { await binding; });
        else Assert.Equal(outcome == "exact", (await binding).IsSuccess);
        Assert.Equal(1, proxy.Runtime.ActiveBindingCount);
        Assert.False(provider.BindDomain(subject).IsSuccess);
        Assert.Equal(1, proxy.RootBindCalls);
        Assert.Equal(0, proxy.CloseCalls); // Returned-object compensation cannot close original invocation.
        if (outcome == "exact")
        {
            Assert.True(provider.RevokeDomain((await binding).Value!).IsSuccess);
            Assert.Equal(0, proxy.Runtime.ActiveBindingCount);
        }
    }
}
