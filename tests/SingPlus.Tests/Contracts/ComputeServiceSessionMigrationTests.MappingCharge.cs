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
    public class MappingChargeNativeSpy : SpyTypes.Dsc1NativeSpy
    {
        public int MappingCloses;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "CloseOwnedRegionMapping") MappingCloses++;
            return base.Invoke(method, args);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedHostMappingFlagCannotSkipUnconsumedOriginalChargeAfterRegionRelease(bool foreignCharge)
    {
        SpyTypes.IOriginalDsc1Runtime runtime = DispatchProxy.Create<SpyTypes.IOriginalDsc1Runtime, MappingChargeNativeSpy>();
        MappingChargeNativeSpy native = (MappingChargeNativeSpy)(object)runtime; native.Native = new();
        SpyTypes.IUninvokedDsc1Provider provider = DispatchProxy.Create<SpyTypes.IUninvokedDsc1Provider, SpyTypes.UninvokedDsc1ProviderSpy>();
        SpyTypes.UninvokedDsc1ProviderSpy spy = (SpyTypes.UninvokedDsc1ProviderSpy)(object)provider; spy.Inner = new HybridCpuPlatformAuthorityProvider(runtime);
        ComponentScenario s = CreateComponentScenario(provider, pinBudget: true); s.Source.Span.Fill(0x63);
        ProcessHandle foreignOwner = TestFixtures.Create(s.Kernel, 932, 9320).Handle;
        OwnedBuffer<byte> foreign = s.Kernel.AllocateBuffer<byte>(foreignOwner, 8).Value!; foreign.Span.Fill(0x7D);
        Task<OwnedBuffer<byte>> response = ComputeServiceClient.Create(new RuntimeSipClientTransport(s.Kernel, s.Caller, s.Session)).CopyAsync(s.Source, s.Destination).AsTask();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo pendingRoute = typeof(RuntimeComputeServiceHost).GetField("_pending", flags)!;
        IDictionary routes = (IDictionary)typeof(RuntimeKernel).GetField("_mappingBudgetReservations", flags)!.GetValue(s.Kernel)!;
        object? charge = null; PlatformRegionMapping mapping = default;
        spy.BeforeSource = () =>
        {
            object? pending = pendingRoute.GetValue(s.Host); if (pending is null) return;
            if (pending.GetType().GetProperty("Outcome")!.GetValue(pending) is null) return;
            mapping = (PlatformRegionMapping)pending.GetType().GetProperty("StagingMapping")!.GetValue(pending)!;
            if (charge is null)
            {
                charge = routes[mapping.MappingId];
                PlatformRegionMapping destinationMapping = (PlatformRegionMapping)pending.GetType().GetProperty("DestinationMapping")!.GetValue(pending)!;
                if (foreignCharge) routes[mapping.MappingId] = routes[destinationMapping.MappingId];
                else routes.Remove(mapping.MappingId);
            }
        };
        Assert.False(s.Host.ProcessNextCopy().IsSuccess); Assert.NotNull(charge);
        spy.BeforeSource = null;
        object owner = pendingRoute.GetValue(s.Host)!;
        KernelResult<PlatformRegionMappingLifecycle> lifecycle = s.Kernel.QueryPlatformRegionMappingLifecycle(s.Service, mapping);
        Assert.True(lifecycle.IsSuccess); Assert.True(lifecycle.Value.LocalReclaimAllowed);
        Assert.Equal(1, native.Native.ActiveOwnedRegionMappingCount); Assert.Equal(1, native.Copies);
        Assert.Equal(1, native.MappingCloses);
        BudgetAccountHandle account = s.Kernel.QueryComponent(new ComponentIdentity("compute-session-test")).Value!.ProcessBudget;
        ulong Used(ServiceBudgetDimension dimension) => s.Kernel.QueryBudget(account).Value!.Usage.Single(u => u.Dimension == dimension).Used;
        Assert.Equal(32UL, Used(ServiceBudgetDimension.PinnedMappedMemoryBytes));
        owner.GetType().GetProperty("StagingMappingClosed")!.SetValue(owner, true);
        Assert.False(s.Host.AdvancePendingCopy().IsSuccess);
        Assert.Equal(1, native.Native.ActiveOwnedRegionMappingCount);
        Assert.Equal(1, native.MappingCloses);
        Assert.Equal(32UL, Used(ServiceBudgetDimension.PinnedMappedMemoryBytes));
        Assert.False(response.IsCompleted); Assert.Throws<InvalidOperationException>(() => { _ = s.Source.Span.Length; });
        Assert.All(foreign.Span.ToArray(), b => Assert.Equal((byte)0x7D, b));
        owner.GetType().GetProperty("StagingMappingClosed")!.SetValue(owner, false);
        routes[mapping.MappingId] = charge;
        KernelResult recovered = s.Host.AdvancePendingCopy(); Assert.True(recovered.IsSuccess, recovered.Message);
        OwnedBuffer<byte> output = await response.WaitAsync(TimeSpan.FromSeconds(5)); Assert.All(output.Span.ToArray(), b => Assert.Equal((byte)0x63, b));
        Assert.Equal(1, native.Copies); Assert.Equal(0, native.Native.ActiveOwnedRegionMappingCount);
        Assert.Equal(2, native.MappingCloses);
        Assert.Equal(0UL, Used(ServiceBudgetDimension.PinnedMappedMemoryBytes)); Assert.Equal(0UL, Used(ServiceBudgetDimension.OwnedMemoryBytes));
        Assert.True(s.Kernel.ReleaseRegion(s.Caller, output).IsSuccess);
        Assert.True(s.Kernel.CloseSession(s.Caller, s.Session).IsSuccess);
        Assert.True(s.Kernel.FaultComponent(new ComponentIdentity("compute-session-test")).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Caller).IsSuccess);
        Assert.All(foreign.Span.ToArray(), b => Assert.Equal((byte)0x7D, b));
    }
}
