using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip.Compute;
using SpyTypes = SingPlus.Tests.Platform.PlatformDsc1ComputeTests;

namespace SingPlus.Tests.Contracts;

public sealed partial class ComputeServiceSessionMigrationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UninvokedSoftwareDsc1OriginalRemainsConsumableByGeneratedService(bool loseView)
    {
        var native = DispatchProxy.Create<SpyTypes.IOriginalDsc1Runtime, SpyTypes.Dsc1NativeSpy>();
        var spy = (SpyTypes.Dsc1NativeSpy)(object)native; spy.Native = new();
        var provider = DispatchProxy.Create<SpyTypes.IUninvokedDsc1Provider, SpyTypes.UninvokedDsc1ProviderSpy>();
        var source = (SpyTypes.UninvokedDsc1ProviderSpy)(object)provider;
        source.Inner = new HybridCpuPlatformAuthorityProvider(native);
        var s = CreateComponentScenario(provider);
        s.Source.Span.Fill(0x51);
        var reply = ComputeServiceClient.Create(new RuntimeSipClientTransport(s.Kernel, s.Caller, s.Session))
            .CopyAsync(s.Source, s.Destination).AsTask();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var payloads = (IDictionary)typeof(RuntimeKernel).GetField("_dsc1PayloadOperations", flags)!.GetValue(s.Kernel)!;
        object? storage = null, original = null; FieldInfo? route = null; var injected = false;
        source.BeforeSource = () =>
        {
            if (injected || payloads.Count != 1) return;
            injected = true;
            if (loseView)
            {
                var payload = Assert.Single(payloads.Values.Cast<object>());
                var lease = payload.GetType().GetProperty("DestinationLease")!.GetValue(payload)!;
                storage = lease.GetType().GetField("_storage", flags)!.GetValue(lease)!;
                route = storage.GetType().GetField("_runtimeReservation", flags)!;
                original = route.GetValue(storage); route.SetValue(storage, null);
            }
            source.SourceUnavailable = true;
        };
        var first = s.Host.ProcessNextCopy();
        Assert.True(injected); Assert.False(first.IsSuccess); Assert.True(s.Host.HasPendingCopy);
        Assert.Single(payloads); Assert.False(reply.IsCompleted);
        var budget = s.Kernel.QueryComponent(new ComponentIdentity("compute-session-test")).Value!.ProcessBudget;
        ulong Used() => s.Kernel.QueryBudget(budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used;
        Assert.Equal(64UL, Used());
        Assert.Throws<InvalidOperationException>(() => s.Source.Span.ToArray());
        if (loseView)
        {
            Assert.False(s.Host.AdvancePendingCopy().IsSuccess);
            Assert.Equal(64UL, Used());
            route!.SetValue(storage, original);
        }
        // The local uninvoked fact closes independently; mapping closure still needs its source.
        Assert.False(s.Host.AdvancePendingCopy().IsSuccess);
        Assert.Empty(payloads); Assert.Equal(32UL, Used()); Assert.True(s.Host.HasPendingCopy);
        Assert.Equal(0, spy.Prepares); Assert.Equal(0, spy.Copies);
        Assert.Equal(0, source.MappingCloseBegins);
        source.SourceUnavailable = false;
        Assert.False(s.Host.AdvancePendingCopy().IsSuccess); // Original request stays rejected.
        Assert.False(s.Host.HasPendingCopy);
        Assert.Equal(2, source.MappingCloseBegins);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await reply);
        Assert.All(s.Source.Span.ToArray(), b => Assert.Equal((byte)0x51, b));
        Assert.Equal(0UL, Used());
        Assert.True(s.Kernel.TerminateProcess(s.Service).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Caller).IsSuccess);
        Assert.Equal(0, spy.Prepares); Assert.Equal(0, spy.Copies);
    }
}
