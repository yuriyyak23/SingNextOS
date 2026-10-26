using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Compute;
using SpyTypes = SingPlus.Tests.Platform.PlatformDsc1ComputeTests;

namespace SingPlus.Tests.Contracts;

public sealed partial class ComputeServiceSessionMigrationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GeneratedServiceOutcomeCannotReplaceOriginalDsc1Consumption(bool cancel)
    {
        SpyTypes.IOriginalDsc1Runtime native =DispatchProxy.Create<SpyTypes.IOriginalDsc1Runtime,SpyTypes.Dsc1NativeSpy>();
        SpyTypes.Dsc1NativeSpy spy =(SpyTypes.Dsc1NativeSpy)(object)native;spy.Native=new();
        ComponentScenario s =CreateComponentScenario(new HybridCpuPlatformAuthorityProvider(native));
        BudgetAccountHandle account =s.Kernel.QueryComponent(new ComponentIdentity("compute-session-test")).Value!.ProcessBudget;
        ulong Used()=>s.Kernel.QueryBudget(account).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        IDictionary payloads =(IDictionary)typeof(RuntimeKernel).GetField("_dsc1PayloadOperations",flags)!.GetValue(s.Kernel)!;
        IDictionary routes =(IDictionary)s.Kernel.Budgets.GetType().GetField("_reservations",flags)!.GetValue(s.Kernel.Budgets)!;
        s.Source.Span.Fill(0x63);
        ProcessHandle foreign =TestFixtures.Create(s.Kernel,2581,2582).Handle;
        OwnedBuffer<byte> independent =s.Kernel.AllocateBuffer<byte>(foreign,16).Value!;independent.Span.Fill(0x7D);
        using CancellationTokenSource cancellation =new CancellationTokenSource();
        Task<OwnedBuffer<byte>> reply =ComputeServiceClient.Create(new RuntimeSipClientTransport(s.Kernel,s.Caller,s.Session,cancellation.Token))
            .CopyAsync(s.Source,s.Destination).AsTask();
        spy.LoseCopyReply=true;spy.LoseQuery=true;
        Assert.False(s.Host.ProcessNextCopy().IsSuccess);
        object pending =typeof(RuntimeComputeServiceHost).GetField("_pending",flags)!.GetValue(s.Host)!;
        PropertyInfo outcome =pending.GetType().GetProperty("Outcome")!;
        outcome.SetValue(pending,cancel?PlatformDsc1CopyOutcome.Cancelled:PlatformDsc1CopyOutcome.Completed);
        Assert.False(s.Host.AdvancePendingCopy().IsSuccess);Assert.Equal(64UL,Used());Assert.True(s.Host.HasPendingCopy);
        Assert.Throws<InvalidOperationException>(()=>s.Source.Span.ToArray());
        spy.LoseCopyReply=false;spy.LoseQuery=false;
        Assert.False(s.Host.AdvancePendingCopy().IsSuccess);Assert.True(s.Host.HasPendingCopy);Assert.Equal(64UL,Used());
        Assert.All(independent.Span.ToArray(),b=>Assert.Equal((byte)0x7D,b));
        outcome.SetValue(pending,null);
        KernelResult retried =s.Host.AdvancePendingCopy();Assert.False(retried.IsSuccess);
        Assert.False(s.Host.HasPendingCopy);Assert.Empty(payloads);Assert.Equal(0UL,Used());Assert.Equal(1,spy.Copies);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await reply.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.All(s.Source.Span.ToArray(),b=>Assert.Equal((byte)0x63,b));
        Assert.All(independent.Span.ToArray(),b=>Assert.Equal((byte)0x7D,b));
        Assert.True(s.Kernel.CloseSession(s.Caller,s.Session).IsSuccess);
        Assert.True(s.Kernel.FaultComponent(new ComponentIdentity("compute-session-test")).IsSuccess);
        Assert.Equal(0,spy.Native.ActiveOwnedRegionMappingCount);Assert.Equal(0,spy.Native.ActiveBindingCount);
        Assert.True(s.Kernel.TerminateProcess(s.Caller).IsSuccess);Assert.True(s.Kernel.TerminateProcess(foreign).IsSuccess);
    }
}


