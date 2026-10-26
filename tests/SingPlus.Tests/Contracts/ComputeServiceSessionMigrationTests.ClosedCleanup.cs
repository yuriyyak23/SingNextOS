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
    [InlineData(false,false,false)] [InlineData(true,false,false)] [InlineData(true,true,false)] [InlineData(true,false,true)]
    public async Task GeneratedComputeCleanupSurvivesOriginalSessionClose(bool unknownCopy,bool copiedCancellation,bool expire)
    {
        SpyTypes.IOriginalDsc1Runtime native =DispatchProxy.Create<SpyTypes.IOriginalDsc1Runtime,SpyTypes.Dsc1NativeSpy>();
        SpyTypes.Dsc1NativeSpy spy =(SpyTypes.Dsc1NativeSpy)(object)native;spy.Native=new();
        PublishedCloseClock? clock =expire?new PublishedCloseClock {UtcNow=new DateTimeOffset(2026,10,17,12,0,0,TimeSpan.Zero)}:null;
        ComponentScenario s =CreateComponentScenario(new HybridCpuPlatformAuthorityProvider(native),clock);
        if(expire)
        {
            Assert.True(s.Kernel.CloseSession(s.Caller,s.Session).IsSuccess);
            KernelResult<EndpointSessionHandle> expiring =s.Kernel.OpenSession(s.Caller,s.Descriptor,[s.CallerCapability],lifetime:TimeSpan.FromSeconds(1));
            Assert.True(expiring.IsSuccess,expiring.Message);
            s=s with {Session=expiring.Value,Host=RuntimeComputeServiceHost.CreateForComponent(s.Kernel,new("compute-session-test"),expiring.Value).Value!};
        }
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
        Assert.False(s.Host.ProcessNextCopy().IsSuccess);Assert.True(s.Host.HasPendingCopy);Assert.Equal(64UL,Used());
        if(expire)
        {
            Assert.Equal(1,spy.Copies);Assert.True(spy.Native.ActiveOwnedRegionMappingCount>0);
            clock!.UtcNow=clock.UtcNow!.Value.AddSeconds(2);
            KernelResult expired =s.Kernel.RequestSessionCancellation(s.Caller,new(s.Session,new(1),new(1)));
            Assert.Equal(KernelError.SessionTimedOut,expired.Error);
            Assert.Equal(EndpointSessionState.Closed,s.Kernel.EndpointSessions.SnapshotLifecycleTrace(s.Session)[^1].State);
            Assert.Equal(64UL,Used());Assert.True(spy.Native.ActiveOwnedRegionMappingCount>0);
            Assert.Equal(RegionState.Owned,s.Kernel.Regions.Snapshot().Single(r=>r.Handle.RegionId==s.Destination.Handle.RegionId).State);
        }
        else Assert.True(s.Kernel.CloseSession(s.Caller,s.Session).IsSuccess);
        if(unknownCopy)
        {
            Assert.False(s.Host.AdvancePendingCopy().IsSuccess);Assert.True(s.Host.HasPendingCopy);Assert.Equal(64UL,Used());
        }
        spy.LoseCopyReply=false;spy.LoseQuery=false;
        if(copiedCancellation)
        {
            // Finish client delivery before substituting the host cleanup fact.
            // Otherwise the waiter legitimately rejects the copied raw too.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await reply.WaitAsync(TimeSpan.FromSeconds(2)));
            object invocations =typeof(RuntimeKernel).GetProperty("SessionInvocations",flags)!.GetValue(s.Kernel)!;
            IDictionary records =(IDictionary)invocations.GetType().GetField("_ownedRecords",flags)!.GetValue(invocations)!;
            object record =records[(s.Session.SessionId,s.Session.Generation,new EndpointSessionInvocationId(1))]!;
            ResponseRegistry.OriginalPublication publication =(ResponseRegistry.OriginalPublication)record.GetType().GetProperty("OriginalResponse")!.GetValue(record)!;
            PropertyInfo property =publication.Completion.GetType().GetProperty("OriginalChannelCancellation")!;
            object raw =property.GetValue(publication.Completion)!;
            property.SetValue(publication.Completion,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(raw,null));
            Assert.False(s.Host.AdvancePendingCopy().IsSuccess);Assert.True(s.Host.HasPendingCopy);Assert.Equal(64UL,Used());Assert.Equal(1,spy.Copies);
            property.SetValue(publication.Completion,raw);
        }
        Assert.All(independent.Span.ToArray(),b=>Assert.Equal((byte)0x7D,b));

        KernelResult retried =s.Host.AdvancePendingCopy();Assert.False(retried.IsSuccess);
        Assert.False(s.Host.HasPendingCopy);Assert.Empty(payloads);Assert.Equal(0UL,Used());Assert.Equal(1,spy.Copies);
        if(expire)Assert.Equal(RegionState.Released,s.Kernel.Regions.Snapshot().Single(r=>r.Handle.RegionId==s.Destination.Handle.RegionId).State);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await reply.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.All(s.Source.Span.ToArray(),b=>Assert.Equal((byte)0x63,b));
        Assert.All(independent.Span.ToArray(),b=>Assert.Equal((byte)0x7D,b));
        Assert.True(s.Kernel.FaultComponent(new ComponentIdentity("compute-session-test")).IsSuccess);
        Assert.Equal(0,spy.Native.ActiveOwnedRegionMappingCount);Assert.Equal(0,spy.Native.ActiveBindingCount);
        Assert.True(s.Kernel.TerminateProcess(s.Caller).IsSuccess);Assert.True(s.Kernel.TerminateProcess(foreign).IsSuccess);
    }
}



