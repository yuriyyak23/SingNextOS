using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Compute;
using SpyTypes = SingPlus.Tests.Platform.PlatformDsc1ComputeTests;

namespace SingPlus.Tests.Contracts;

public sealed partial class ComputeServiceSessionMigrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task GeneratedCopyRequiresPublishedKernelOutputAfterOriginalBackingRecovery(bool lateRelease, bool lostCopyReply)
    {
        SpyTypes.IOriginalDsc1Runtime runtime = DispatchProxy.Create<SpyTypes.IOriginalDsc1Runtime, SpyTypes.Dsc1NativeSpy>();
        SpyTypes.Dsc1NativeSpy native = (SpyTypes.Dsc1NativeSpy)(object)runtime; native.Native = new();
        native.LoseCopyReply = lostCopyReply;
        SpyTypes.IUninvokedDsc1Provider provider = DispatchProxy.Create<SpyTypes.IUninvokedDsc1Provider, SpyTypes.UninvokedDsc1ProviderSpy>();
        SpyTypes.UninvokedDsc1ProviderSpy source = (SpyTypes.UninvokedDsc1ProviderSpy)(object)provider;
        source.Inner = new HybridCpuPlatformAuthorityProvider(runtime);
        ComponentScenario s = CreateComponentScenario(provider);
        s.Source.Span.Fill(0x63); s.Destination.Span.Fill(0x4D);
        ProcessHandle foreignOwner = TestFixtures.Create(s.Kernel, 931, 9310).Handle;
        OwnedBuffer<byte> foreign = s.Kernel.AllocateBuffer<byte>(foreignOwner, 8).Value!; foreign.Span.Fill(0x7D);
        RegionHandle destination = s.Destination.Handle;
        Task<OwnedBuffer<byte>> response = ComputeServiceClient.Create(new RuntimeSipClientTransport(s.Kernel, s.Caller, s.Session))
            .CopyAsync(s.Source, s.Destination).AsTask();
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary operations = (IDictionary)s.Kernel.PlatformAuthority.GetType().GetField("_dsc1Operations", hidden)!.GetValue(s.Kernel.PlatformAuthority)!;
        object? operation = null;
        PlatformOriginalDsc1CopyBackingObservation? original = null;
        int reads = 0;
        source.BeforeSource = () =>
        {
            if (operations.Count != 1) return;
            object current = operations.Values.Cast<object>().Single();
            if (!(bool)current.GetType().GetProperty("PublicationAdmitted")!.GetValue(current)!) return;
            if (++reads != (lateRelease ? 2 : 1)) return;
            operation = current;
            PropertyInfo raw = current.GetType().GetProperty("OriginalBackingObservation")!;
            original = (PlatformOriginalDsc1CopyBackingObservation)raw.GetValue(current)!;
            raw.SetValue(current, original with { });
        };
        KernelResult refused = s.Host.ProcessNextCopy();
        Assert.False(refused.IsSuccess); Assert.NotNull(original); Assert.True(s.Host.HasPendingCopy);
        Assert.False(response.IsCompleted); Assert.Equal(1, native.Copies);
        ulong Used() => s.Kernel.QueryBudget(s.Kernel.QueryComponent(new ComponentIdentity("compute-session-test")).Value!.ProcessBudget)
            .Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used;
        Assert.Equal(64UL, Used());
        Assert.Throws<InvalidOperationException>(() => { _ = s.Source.Span.Length; });
        Assert.All(foreign.Span.ToArray(), b => Assert.Equal((byte)0x7D, b));
        source.BeforeSource = null;
        operation!.GetType().GetProperty("OriginalBackingObservation")!.SetValue(operation, original);
        KernelResult recovered = s.Host.AdvancePendingCopy();
        Assert.Equal(!lateRelease, recovered.IsSuccess);
        Assert.False(s.Host.HasPendingCopy); Assert.Equal(1, native.Copies); Assert.Equal(0UL, Used());
        Assert.All(s.Source.Span.ToArray(), b => Assert.Equal((byte)0x63, b));
        if (lateRelease)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await response.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(r => r.Handle.RegionId == destination.RegionId).State);
        }
        else
        {
            OwnedBuffer<byte> output = await response.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(output.Span.ToArray(), b => Assert.Equal((byte)0x63, b));
            Assert.True(s.Kernel.ReleaseRegion(s.Caller, output).IsSuccess);
        }
        Assert.Equal(0, native.Native.ActiveOwnedRegionMappingCount);
        Assert.True(s.Kernel.CloseSession(s.Caller, s.Session).IsSuccess);
        Assert.True(s.Kernel.FaultComponent(new ComponentIdentity("compute-session-test")).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Caller).IsSuccess);
        Assert.All(foreign.Span.ToArray(), b => Assert.Equal((byte)0x7D, b));
    }
}
