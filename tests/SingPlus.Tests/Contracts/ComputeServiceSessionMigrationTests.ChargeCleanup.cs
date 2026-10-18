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
    public async Task GeneratedServiceRetainsPostEffectOriginalUntilStagingChargeSettlement(bool cancel)
    {
        var native=DispatchProxy.Create<SpyTypes.IOriginalDsc1Runtime,SpyTypes.Dsc1NativeSpy>();
        var spy=(SpyTypes.Dsc1NativeSpy)(object)native;spy.Native=new();
        var s=CreateComponentScenario(new HybridCpuPlatformAuthorityProvider(native));
        var account=s.Kernel.QueryComponent(new ComponentIdentity("compute-session-test")).Value!.ProcessBudget;
        ulong Used()=>s.Kernel.QueryBudget(account).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var payloads=(IDictionary)typeof(RuntimeKernel).GetField("_dsc1PayloadOperations",flags)!.GetValue(s.Kernel)!;
        var routes=(IDictionary)s.Kernel.Budgets.GetType().GetField("_reservations",flags)!.GetValue(s.Kernel.Budgets)!;
        BudgetReservationHandle charge=default;object? original=null;
        s.Source.Span.Fill(0x63);
        var foreign=TestFixtures.Create(s.Kernel,2581,2582).Handle;
        var independent=s.Kernel.AllocateBuffer<byte>(foreign,16).Value!;independent.Span.Fill(0x7D);
        using var cancellation=new CancellationTokenSource();
        var reply=ComputeServiceClient.Create(new RuntimeSipClientTransport(s.Kernel,s.Caller,s.Session,cancellation.Token))
            .CopyAsync(s.Source,s.Destination).AsTask();
        spy.AfterCopy=()=>
        {
            spy.AfterCopy=null;
            var payload=Assert.Single(payloads.Values.Cast<object>());
            charge=(BudgetReservationHandle)payload.GetType().GetProperty("StagedOutputCharge")!.GetValue(payload)!;
            original=routes[charge.ReservationId]!;routes.Remove(charge.ReservationId);
        };
        Assert.False(s.Host.ProcessNextCopy().IsSuccess);Assert.NotNull(original);
        Assert.True(s.Host.HasPendingCopy);Assert.Single(payloads);Assert.Equal(64UL,Used());Assert.Equal(1,spy.Copies);
        Assert.False(reply.IsCompleted);Assert.Throws<InvalidOperationException>(()=>s.Source.Span.ToArray());
        if(cancel)cancellation.Cancel();
        Assert.False(s.Host.AdvancePendingCopy().IsSuccess);
        Assert.True(s.Host.HasPendingCopy);Assert.Single(payloads);Assert.Equal(64UL,Used());Assert.Equal(1,spy.Copies);
        Assert.All(independent.Span.ToArray(),b=>Assert.Equal((byte)0x7D,b));
        routes[charge.ReservationId]=original;
        var retried=s.Host.AdvancePendingCopy();Assert.True(retried.IsSuccess,retried.Message);
        Assert.False(s.Host.HasPendingCopy);Assert.Empty(payloads);Assert.Equal(0UL,Used());Assert.Equal(1,spy.Copies);
        if(cancel)await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await reply.WaitAsync(TimeSpan.FromSeconds(2)));
        else
        {
            var result=await reply.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.All(result.Span.ToArray(),b=>Assert.Equal((byte)0x63,b));
            Assert.True(s.Kernel.ReleaseRegion(s.Caller,result).IsSuccess);
        }
        Assert.All(s.Source.Span.ToArray(),b=>Assert.Equal((byte)0x63,b));
        Assert.All(independent.Span.ToArray(),b=>Assert.Equal((byte)0x7D,b));
        Assert.True(s.Kernel.CloseSession(s.Caller,s.Session).IsSuccess);
        Assert.True(s.Kernel.FaultComponent(new ComponentIdentity("compute-session-test")).IsSuccess);
        Assert.Equal(0,spy.Native.ActiveOwnedRegionMappingCount);Assert.Equal(0,spy.Native.ActiveBindingCount);
        Assert.True(s.Kernel.TerminateProcess(s.Caller).IsSuccess);Assert.True(s.Kernel.TerminateProcess(foreign).IsSuccess);
    }
}
