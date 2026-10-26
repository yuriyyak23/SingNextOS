using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Compute;
using SpyTypes =SingPlus.Tests.Platform.PlatformDsc1ComputeTests;

namespace SingPlus.Tests.Contracts;
public sealed partial class ComputeServiceSessionMigrationTests
{
    [Theory]
    [InlineData(true,false,false,"none")] [InlineData(false,false,false,"none")] [InlineData(false,true,false,"none")] [InlineData(false,false,true,"none")]
    [InlineData(false,true,false,"expiry")] [InlineData(false,true,false,"close")] [InlineData(false,false,false,"route-loss")]
    public async Task PendingNativeCopyRequiresFreshExpiryForNewOwnershipPublication(bool unknownNative,bool expireOnRecovery,bool failAfterHook,string admissionRace)
    {
        SpyTypes.IOriginalDsc1Runtime native =DispatchProxy.Create<SpyTypes.IOriginalDsc1Runtime,SpyTypes.Dsc1NativeSpy>();
        SpyTypes.Dsc1NativeSpy spy =(SpyTypes.Dsc1NativeSpy)(object)native;spy.Native=new();
        PublishedCloseClock clock =new PublishedCloseClock {UtcNow=new DateTimeOffset(2026,10,17,12,0,0,TimeSpan.Zero)};
        ComponentScenario s =CreateComponentScenario(new HybridCpuPlatformAuthorityProvider(native),clock);
        Assert.True(s.Kernel.CloseSession(s.Caller,s.Session).IsSuccess);
        KernelResult<EndpointSessionHandle> expiring =s.Kernel.OpenSession(s.Caller,s.Descriptor,[s.CallerCapability],lifetime:TimeSpan.FromSeconds(1));
        Assert.True(expiring.IsSuccess,expiring.Message);
        s=s with {Session=expiring.Value,Host=RuntimeComputeServiceHost.CreateForComponent(s.Kernel,new("compute-session-test"),expiring.Value).Value!};
        BudgetAccountHandle account =s.Kernel.QueryComponent(new("compute-session-test")).Value!.ProcessBudget;
        ulong Used()=>s.Kernel.QueryBudget(account).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        ProcessHandle foreign =TestFixtures.Create(s.Kernel,2591,2592).Handle;
        OwnedBuffer<byte> independent =s.Kernel.AllocateBuffer<byte>(foreign,16).Value!;independent.Span.Fill(0x7D);s.Source.Span.Fill(0x63);
        Task<OwnedBuffer<byte>> reply =ComputeServiceClient.Create(new RuntimeSipClientTransport(s.Kernel,s.Caller,s.Session)).CopyAsync(s.Source,s.Destination).AsTask();
        EndpointSessionInvocationHandle invocation =new EndpointSessionInvocationHandle(s.Session,new(1),new(1));
        if(unknownNative)
        {
            spy.LoseCopyReply=true;spy.LoseQuery=true;
            Assert.False(s.Host.ProcessNextCopy().IsSuccess);Assert.Equal(64UL,Used());
        }
        else
        {
            s.Kernel.SessionInvocationSettlementReservedHook=()=>
            {
                s.Kernel.SessionInvocationSettlementReservedHook=null;
                throw new IOException("Original response action interrupted before start");
            };
            Assert.Throws<IOException>(()=>s.Host.ProcessNextCopy());Assert.Equal(16UL,Used());
        }
        Assert.Equal(1,spy.Copies);
        clock.ThrowUtc=true;
        Assert.Equal(KernelError.PlatformFaulted,s.Kernel.RequestSessionCancellation(s.Caller,invocation).Error);
        spy.LoseCopyReply=false;spy.LoseQuery=false;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        global::System.Collections.IDictionary? lostRoutes=null;object? lostRecord=null;
        (EndpointSessionId SessionId, EndpointSessionGeneration Generation, EndpointSessionInvocationId) routeKey =(s.Session.SessionId,s.Session.Generation,new EndpointSessionInvocationId(1));
        int observed =0;
        if(admissionRace=="route-loss")clock.ObserveUtc=()=>
        {
            clock.ObserveUtc=null;observed++;
            object registry =typeof(RuntimeKernel).GetProperty("SessionInvocations",flags)!.GetValue(s.Kernel)!;
            lostRoutes=(global::System.Collections.IDictionary)registry.GetType().GetField("_records",flags)!.GetValue(registry)!;
            lostRecord=lostRoutes[routeKey]!;lostRoutes.Remove(routeKey);
        };
        if(failAfterHook)
        {
            clock.ThrowUtc=false;
            s.Kernel.SessionInvocationSettlementReservedHook=()=>
            {
                s.Kernel.SessionInvocationSettlementReservedHook=null;clock.ThrowUtc=true;
            };
        }
        KernelResult resumedWhileUnknown =s.Host.AdvancePendingCopy();Assert.False(resumedWhileUnknown.IsSuccess);
        if(unknownNative)
        {
            Assert.False(s.Host.HasPendingCopy);Assert.Equal(0UL,Used());Assert.Equal(1,spy.Copies);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>reply);
            Assert.Equal(RegionState.Released,s.Kernel.Regions.Snapshot().Single(r=>r.Handle.RegionId==s.Destination.Handle.RegionId).State);
            clock.ThrowUtc=false;
            Assert.True(s.Kernel.CloseSession(s.Caller,s.Session).IsSuccess);
            Assert.All(s.Source.Span.ToArray(),b=>Assert.Equal((byte)0x63,b));
            Assert.All(independent.Span.ToArray(),b=>Assert.Equal((byte)0x7D,b));
            Assert.True(s.Kernel.FaultComponent(new("compute-session-test")).IsSuccess);
            Assert.Equal(0,spy.Native.ActiveOwnedRegionMappingCount);Assert.Equal(0,spy.Native.ActiveBindingCount);
            Assert.True(s.Kernel.TerminateProcess(s.Caller).IsSuccess);Assert.True(s.Kernel.TerminateProcess(foreign).IsSuccess);
            return;
        }
        Assert.True(s.Host.HasPendingCopy);Assert.False(reply.IsCompleted);Assert.Equal(1,spy.Copies);
        Assert.Equal(16UL,Used());Assert.Equal(0,spy.Native.ActiveOwnedRegionMappingCount);
        Assert.Equal(RegionState.Owned,s.Kernel.Regions.Snapshot().Single(r=>r.Handle.RegionId==s.Destination.Handle.RegionId).State);
        Assert.All(independent.Span.ToArray(),b=>Assert.Equal((byte)0x7D,b));
        if(lostRoutes is not null){Assert.Equal(1,observed);lostRoutes[routeKey]=lostRecord;}
        clock.ThrowUtc=false;
        if(expireOnRecovery)
        {
            clock.UtcNow=clock.UtcNow!.Value.AddSeconds(2);
            if(admissionRace is "expiry" or "close")
            {
                if(admissionRace=="close")clock.ObserveUtc=()=>
                {
                    clock.ObserveUtc=null;observed++;
                    Assert.False(Monitor.IsEntered(typeof(RuntimeKernel).GetField("_requestResponseCorrelationGate",flags)!.GetValue(s.Kernel)!));
                    Assert.Equal(KernelError.InvalidTransition,s.Host.AdvancePendingCopy().Error);
                    Task<KernelResult> worker =Task.Run(()=>s.Kernel.CloseSession(s.Caller,s.Session));
                    Assert.True(worker.Wait(TimeSpan.FromSeconds(5)),"Expiry callback held a commit lock");
                    Assert.True(worker.Result.IsSuccess,worker.Result.Message);
                };
                Assert.False(s.Host.AdvancePendingCopy().IsSuccess);Assert.True(s.Host.HasPendingCopy);Assert.Equal(16UL,Used());
                Assert.Equal(EndpointSessionState.Closed,s.Kernel.EndpointSessions.SnapshotLifecycleTrace(s.Session)[^1].State);
                if(admissionRace=="close")Assert.Equal(1,observed);
            }
            else Assert.Equal(KernelError.SessionTimedOut,s.Kernel.RequestSessionCancellation(s.Caller,invocation).Error);
            KernelResult cleaned =s.Host.AdvancePendingCopy();Assert.True(cleaned.IsSuccess,cleaned.Message);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>reply);
            Assert.Equal(RegionState.Released,s.Kernel.Regions.Snapshot().Single(r=>r.Handle.RegionId==s.Destination.Handle.RegionId).State);
        }
        else
        {
            KernelResult resumed =s.Host.AdvancePendingCopy();Assert.True(resumed.IsSuccess,resumed.Message);
            OwnedBuffer<byte> output =await reply;Assert.All(output.Span.ToArray(),b=>Assert.Equal((byte)0x63,b));
            Assert.True(s.Kernel.ReleaseRegion(s.Caller,output).IsSuccess);
            Assert.True(s.Kernel.CloseSession(s.Caller,s.Session).IsSuccess);
        }
        Assert.False(s.Host.HasPendingCopy);Assert.Equal(0UL,Used());Assert.Equal(1,spy.Copies);
        Assert.All(s.Source.Span.ToArray(),b=>Assert.Equal((byte)0x63,b));
        Assert.All(independent.Span.ToArray(),b=>Assert.Equal((byte)0x7D,b));
        Assert.True(s.Kernel.FaultComponent(new("compute-session-test")).IsSuccess);
        Assert.Equal(0,spy.Native.ActiveOwnedRegionMappingCount);Assert.Equal(0,spy.Native.ActiveBindingCount);
        Assert.True(s.Kernel.TerminateProcess(s.Caller).IsSuccess);Assert.True(s.Kernel.TerminateProcess(foreign).IsSuccess);
    }
}
