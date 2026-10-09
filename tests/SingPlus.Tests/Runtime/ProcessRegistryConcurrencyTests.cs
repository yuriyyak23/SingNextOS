using System.Collections.Concurrent;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class ProcessRegistryConcurrencyTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LostDomainRouteCannotReclaimLiveSiblingBacking(bool replace)
    {
        var kernel = new RuntimeKernel();
        var first = TestFixtures.Create(kernel, 1, 10).Handle;
        var second = TestFixtures.Create(kernel, 2, 10).Handle;
        _ = kernel.AllocateBuffer<byte>(second, 4).Value!;
        var routes = (global::System.Collections.IDictionary)typeof(DomainRegistry)
            .GetField("_domains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.Domains)!;
        if (replace) routes[new DomainId(10)] = new HashSet<ProcessHandle> { first };
        else routes.Remove(new DomainId(10));
        Assert.True(kernel.TerminateProcess(first).IsSuccess);
        Assert.True(kernel.Processes.Resolve(second).IsSuccess);
        Assert.NotEqual(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.True(kernel.Domains.Contains(new(10)));
        Assert.True(kernel.TerminateProcess(second).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.True(kernel.CreateProcess(TestFixtures.Manifest(3, 10)).IsSuccess);
        Assert.True(kernel.Domains.Contains(new(10)));
        Assert.True(kernel.TerminateProcess(new(new(3), 1)).IsSuccess);
    }

    [Fact]
    public void ForeignRetirementCannotPublishOriginalTerminalFactAndReceiptSurvivesRouteLoss()
    {
        var kernel = new RuntimeKernel();
        var (original, handle) = TestFixtures.Create(kernel, 1, 10);
        _ = kernel.AllocateBuffer<byte>(handle, 4).Value!;
        var foreignKernel = new RuntimeKernel();
        var (foreign, _) = TestFixtures.Create(foreignKernel, 1, 10);
        Assert.True(foreignKernel.TerminateProcess(handle).IsSuccess);
        kernel.Processes.Retire(foreign);
        Assert.Same(original, kernel.Processes.Resolve(handle).Value);
        Assert.False(kernel.Processes.ResolveTerminalState(handle).IsSuccess);
        Assert.NotEqual(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.True(kernel.TerminateProcess(handle).IsSuccess);
        var routes = (global::System.Collections.IDictionary)typeof(ProcessRegistry)
            .GetField("_terminalStates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.Processes)!;
        routes.Remove(handle);
        Assert.True(kernel.CreateProcess(TestFixtures.Manifest(1, 10, 2)).IsSuccess);
        Assert.Equal(ProcessState.Exited, kernel.Processes.ResolveTerminalState(handle).Value);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ForeignOrCopiedProcessRouteCannotRetireOriginalBacking(bool copy)
    {
        var kernel = new RuntimeKernel();
        var (original, handle) = TestFixtures.Create(kernel, 1, 10);
        _ = kernel.AllocateBuffer<byte>(handle, 4).Value!;
        var foreignKernel = new RuntimeKernel();
        var (foreign, _) = TestFixtures.Create(foreignKernel, 1, 10);
        var routes = (global::System.Collections.IDictionary)typeof(ProcessRegistry)
            .GetField("_live", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.Processes)!;
        routes[handle.ProcessId] = copy ? original.GetType()
            .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(original, null)! : foreign;
        Assert.False(kernel.TerminateProcess(handle).IsSuccess);
        Assert.False(kernel.Processes.ResolveTerminalState(handle).IsSuccess);
        Assert.NotEqual(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        routes[handle.ProcessId] = original;
        Assert.True(kernel.TerminateProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Exited, kernel.Processes.ResolveTerminalState(handle).Value);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.True(foreignKernel.Processes.Resolve(handle).IsSuccess);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void ConcurrentCreationRefusesCollisionBeforePartialRegistryMutation(bool kernelConsumer, bool identity)
    {
        var kernel = new RuntimeKernel();
        var registry = kernelConsumer ? kernel.Processes : new ProcessRegistry();
        var results = new ConcurrentBag<KernelResult<SingProcess>>();
        Parallel.For(0, 32, i =>
        {
            var manifest = TestFixtures.Manifest(identity ? (ulong)i + 1 : 1, (ulong)i + 10,
                identity: identity ? "same-identity" : $"entry-{i}");
            results.Add(kernelConsumer ? kernel.CreateProcess(manifest) : registry.Create(manifest));
        });
        var successful = Assert.Single(results, r => r.IsSuccess).Value!;
        Assert.Equal(31, results.Count(r => r.Error == (identity ? KernelError.DuplicateIdentity : KernelError.DuplicateProcessId)));
        Assert.Same(successful, Assert.Single(registry.Snapshot()));
        Assert.Same(successful, registry.Resolve(new(successful.ProcessId, successful.Generation)).Value);
        if (kernelConsumer) Assert.Equal(successful.DomainId, Assert.Single(kernel.Domains.Snapshot()));
    }

    [Fact]
    public async Task CreationSharesLifecycleCommitWhileOwnerMetadataQueriesRemainIndependent()
    {
        var kernel = new RuntimeKernel();
        var (_, old) = TestFixtures.Create(kernel, 1, 10);
        var gate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => { lock (gate) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); } });
        Task<KernelResult<SingProcess>> creation = null!;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            creation = Task.Run(() => { started.SetResult(); return kernel.CreateProcess(TestFixtures.Manifest(2, 20)); });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() =>
            {
                Assert.True(kernel.Processes.Resolve(old).IsSuccess);
                Assert.Single(kernel.Processes.Snapshot());
                Assert.True(kernel.Domains.Contains(new(10)));
                Assert.Single(kernel.Domains.Snapshot());
            }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(creation.IsCompleted);
        }
        finally { release.Set(); }
        await holder;
        Assert.True((await creation).IsSuccess);
        Assert.Equal(2, kernel.Processes.Snapshot().Count);
        Assert.Equal(2, kernel.Domains.Snapshot().Count);
    }

    [Fact]
    public async Task ConcurrentOwnerSnapshotsRemainValidAcrossActualCreateRetireAndGenerationReuse()
    {
        var kernel = new RuntimeKernel();
        using var stop = new CancellationTokenSource();
        using var reading = new ManualResetEventSlim();
        var reads = 0;
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var processes = kernel.Processes.Snapshot();
                Assert.Equal(processes.Count, processes.Select(p => p.ProcessId).Distinct().Count());
                var domains = kernel.Domains.Snapshot();
                Assert.Equal(domains.Count, domains.Distinct().Count());
                foreach (var p in processes) _ = kernel.Processes.Resolve(new(p.ProcessId, p.Generation));
                foreach (var d in domains) _ = kernel.Domains.Contains(d);
                Interlocked.Increment(ref reads);
                reading.Set();
            }
        });
        try
        {
            Assert.True(reading.Wait(TimeSpan.FromSeconds(5)));
            await Task.Run(() => Parallel.For(0, 32, i =>
                Assert.True(kernel.CreateProcess(TestFixtures.Manifest((ulong)i + 1, 10)).IsSuccess)));
            Assert.Equal(32, kernel.Processes.Snapshot().Count);
            Assert.Single(kernel.Domains.Snapshot());
            await Task.Run(() => Parallel.For(0, 32, i =>
                Assert.True(kernel.TerminateProcess(new(new((ulong)i + 1), 1)).IsSuccess)));
            Assert.Empty(kernel.Processes.Snapshot());
            Assert.Empty(kernel.Domains.Snapshot());
            await Task.Run(() => Parallel.For(0, 32, i =>
                Assert.True(kernel.CreateProcess(TestFixtures.Manifest((ulong)i + 1, 10, 2)).IsSuccess)));
            for (ulong i = 1; i <= 32; i++)
            {
                Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(new(new(i), 1)).Error);
                Assert.Equal(ProcessState.Exited, kernel.Processes.ResolveTerminalState(new(new(i), 1)).Value);
                Assert.True(kernel.Processes.Resolve(new(new(i), 2)).IsSuccess);
            }
            Assert.Equal(32, kernel.Processes.Snapshot().Count);
            Assert.Single(kernel.Domains.Snapshot());
        }
        finally { stop.Cancel(); }
        await reader.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(reads > 0);
    }
}
