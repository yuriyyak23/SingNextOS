using System.Collections;
using SingPlus.Contracts;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class RootTransitionContinuityTests
{
    [Theory]
    [InlineData("exact")][InlineData("owner")][InlineData("absent")]
    [InlineData("unknown")][InlineData("false-success")][InlineData("false-decision")]
    [InlineData("lease")][InlineData("transition")][InlineData("state")]
    [InlineData("revoked")][InlineData("null")][InlineData("throw")]
    public async Task TransitionRetainsOriginalOutcomeAndInterlocksRootClose(string outcome)
    {
        INeutralDomainRuntime runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        ChildClosureTupleTests.RootCloseRuntimeProxy proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        IDictionary owners = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(provider)!;
        object original = owners[parent.LeaseId]!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        proxy.RootTransitionOutcome = outcome;
        proxy.BeforeRootTransition = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Root transition callback timeout.");
            if (outcome == "owner") owners[parent.LeaseId] = Activator.CreateInstance(original.GetType(), parent,
                original.GetType().GetProperty("HybridCpuLease")!.GetValue(original))!;
            if (outcome == "absent") owners.Remove(parent.LeaseId);
        };
        Task<PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>> invocation = Task.Run(() => provider.TransitionDomainExecution(parent, PlatformDomainExecutionTransition.Start));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False((await Task.Run(() => provider.RevokeDomain(parent)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.False(provider.TransitionDomainExecution(parent, PlatformDomainExecutionTransition.Start).IsSuccess);
            Assert.False(provider.BindDevice(parent, new("device"), PlatformDeviceRights.Configure).IsSuccess);
            Assert.Equal(1, proxy.RootTransitionCalls);
            Assert.Equal(0, proxy.CloseCalls);
            Assert.Equal(0, proxy.DeviceBindCalls);
        }
        finally { release.Set(); }
        if (outcome is "throw" or "null") await Assert.ThrowsAnyAsync<Exception>(async () => { await invocation; });
        else Assert.Equal(outcome == "exact", (await invocation).IsSuccess);
        proxy.BeforeRootTransition = null;
        if (outcome is "owner" or "absent")
        {
            Assert.False(provider.TransitionDomainExecution(parent, PlatformDomainExecutionTransition.Start).IsSuccess);
            owners[parent.LeaseId] = original;
            Assert.Equal(0, proxy.CloseCalls);
            Assert.False(provider.TransitionDomainExecution(parent, PlatformDomainExecutionTransition.Park).IsSuccess);
            Assert.True(provider.TransitionDomainExecution(parent, PlatformDomainExecutionTransition.Start).IsSuccess);
            Assert.Equal(1, proxy.RootTransitionCalls);
        }
        else if (outcome != "exact")
        {
            Assert.False(provider.TransitionDomainExecution(parent, PlatformDomainExecutionTransition.Start).IsSuccess);
            Assert.False(provider.BindDevice(parent, new("device"), PlatformDeviceRights.Configure).IsSuccess);
            Assert.Equal(1, proxy.RootTransitionCalls);
        }
        Assert.Equal(1, proxy.Runtime.ActiveBindingCount); // Transition response never closes the original domain.
        Assert.True(provider.RevokeDomain(parent).IsSuccess); // Separate exact neutral close is the cleanup consumer.
        Assert.Equal(1, proxy.CloseCalls);
        Assert.Equal(0, proxy.Runtime.ActiveBindingCount);
    }

    [Fact]
    public void InvalidTransitionIsNoEffectAndKeepsFreshStartParkResumeUsable()
    {
        INeutralDomainRuntime runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        ChildClosureTupleTests.RootCloseRuntimeProxy proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        Assert.False(provider.TransitionDomainExecution(parent, (PlatformDomainExecutionTransition)777).IsSuccess);
        Assert.Equal(0, proxy.RootTransitionCalls);
        Assert.Equal(PlatformAuthorityStatus.Denied, provider.TransitionDomainExecution(parent, PlatformDomainExecutionTransition.Resume).Status);
        Assert.True(provider.TransitionDomainExecution(parent, PlatformDomainExecutionTransition.Start).IsSuccess);
        Assert.True(provider.TransitionDomainExecution(parent, PlatformDomainExecutionTransition.Park).IsSuccess);
        Assert.True(provider.TransitionDomainExecution(parent, PlatformDomainExecutionTransition.Resume).IsSuccess);
        Assert.Equal(4, proxy.RootTransitionCalls);
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
    }
}
