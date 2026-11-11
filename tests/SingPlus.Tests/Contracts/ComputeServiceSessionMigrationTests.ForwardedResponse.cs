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
    [InlineData(false,false,false)] [InlineData(true,false,false)] [InlineData(false,true,false)] [InlineData(false,true,true)]
    public async Task ForwardedOwnershipKeepsHistoricalResponseWithoutOldWrapperAuthority(bool copiedRaw,bool cancelAfterPublication,bool expireAfterPublication)
    {
        SpyTypes.IOriginalDsc1Runtime runtime =DispatchProxy.Create<SpyTypes.IOriginalDsc1Runtime,SpyTypes.Dsc1NativeSpy>();
        SpyTypes.Dsc1NativeSpy spy =(SpyTypes.Dsc1NativeSpy)(object)runtime;spy.Native=new();
        PublishedCloseClock? clock =cancelAfterPublication?new PublishedCloseClock():null;
        ComponentScenario s =CreateComponentScenario(new HybridCpuPlatformAuthorityProvider(runtime),clock);s.Source.Span.Fill(0x63);
        if(expireAfterPublication)
        {
            clock!.UtcNow=new DateTimeOffset(2026,10,17,12,0,0,TimeSpan.Zero);
            Assert.True(s.Kernel.CloseSession(s.Caller,s.Session).IsSuccess);
            KernelResult<EndpointSessionHandle> expiring =s.Kernel.OpenSession(s.Caller,s.Descriptor,[s.CallerCapability],lifetime:TimeSpan.FromSeconds(1));
            Assert.True(expiring.IsSuccess,expiring.Message);
            s=s with {Session=expiring.Value,Host=RuntimeComputeServiceHost.CreateForComponent(s.Kernel,new("compute-session-test"),expiring.Value).Value!};
        }
        ProcessHandle third =TestFixtures.Create(s.Kernel,927,9270).Handle;AdmitCallerBudget(s.Kernel,third,9270);
        OwnedBuffer<byte> independent =s.Kernel.AllocateBuffer<byte>(third,4).Value!;independent.Span.Fill(0x7D);
        EndpointSessionRegistry.Record session =s.Kernel.ResolveSession(s.Caller,s.Session).Value!;
        ResponseRegistry responses =(ResponseRegistry)typeof(RuntimeKernel).GetProperty("Responses",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        using CancellationTokenSource cancellation =new CancellationTokenSource();
        EndpointSessionInvocationHandle invocation =new EndpointSessionInvocationHandle(s.Session,new(1),new(1));
        ResponseRegistry.OriginalPublication? original=null; int observers =0;
        if(clock is not null)
        {
            Assert.True(s.Kernel.StartTraceSession(s.Caller,32).IsSuccess);
            clock.Observe=()=>
            {
                if(s.Kernel.SnapshotSessionInvocationConsequence(invocation) is null)return;
                clock.Observe=null;observers++;
                original=responses.CapturePublication(s.Service,session.ServiceEndpoint,1).Value!;
                Assert.True(s.Host.ProcessNextCopy().IsSuccess);
                if(expireAfterPublication)clock.UtcNow=clock.UtcNow!.Value.AddSeconds(2);
                cancellation.Cancel(); // Before InvokeSessionOwnershipPairAsync registers this token.
            };
        }
        ValueTask<OwnedBuffer<byte>> reply =ComputeServiceClient.Create(new RuntimeSipClientTransport(s.Kernel,s.Caller,s.Session,cancellation.Token)).CopyAsync(s.Source,s.Destination);
        if(!cancelAfterPublication)
        {
            original=responses.CapturePublication(s.Service,session.ServiceEndpoint,1).Value!;
            Assert.True(s.Host.ProcessNextCopy().IsSuccess);
        }
        OwnedBuffer<byte> output =await reply;Assert.NotNull(original);
        Assert.Equal(cancelAfterPublication?1:0,observers);Assert.Equal(1,spy.Copies);
        if(expireAfterPublication)
        {
            Assert.Equal(EndpointSessionState.Closed,s.Kernel.EndpointSessions.SnapshotLifecycleTrace(s.Session)[^1].State);
            Assert.False(s.Kernel.Channels.GetEndpoint(session.Channel).IsSuccess);
        }
        ResponseEnvelope raw =responses.ReadPublication(original).Value!;Assert.Same(output,raw.Payload);
        RegionHandle originalHandle =output.Handle;
        (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation) originalCharge =Assert.Single(s.Kernel.Budgets.OriginalRegionCharges(originalHandle));
        ProtocolDefinitionV1 protocol =new ProtocolDefinitionV1("ForwardComputeResult","forward-result","Ready",null,
            [new(1,"Move",consumes:["payload"],requestPayload:new(RequestPayloadKind.Ownership,"payload","SingPlus.Sip.OwnedBuffer",ownershipPayloadKind:OwnershipPayloadKind.OwnedBuffer))],
            [new(1,"Ready","Ready")]);
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) channel =s.Kernel.CreateChannel(s.Caller,third,protocol,2).Value;
        KernelResult<ChannelEnvelope> sent =s.Kernel.Send(s.Caller,third,channel.Left,1,output);Assert.True(sent.IsSuccess,sent.Message);
        KernelResult<ChannelEnvelope> received =s.Kernel.Receive(third,channel.Right);Assert.True(received.IsSuccess,received.Message);
        OwnedBuffer<byte> forwarded =Assert.IsType<OwnedBuffer<byte>>(received.Value!.Payload);
        Assert.Equal(originalHandle.RegionId,forwarded.Handle.RegionId);Assert.True(forwarded.Handle.Generation.Value>originalHandle.Generation.Value);
        Assert.False(output.IsValid);Assert.Throws<InvalidOperationException>(()=>output.Span.ToArray());
        Assert.False(s.Kernel.ReleaseRegion(s.Caller,output).IsSuccess);
        Assert.Equal(BudgetReservationState.Released,s.Kernel.QueryBudget(originalCharge.Reservation).Value!.State);
        (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation) forwardedCharge =Assert.Single(s.Kernel.Budgets.OriginalRegionCharges(forwarded.Handle));
        Assert.Equal(BudgetReservationState.Active,s.Kernel.QueryBudget(forwardedCharge.Reservation).Value!.State);
        if(cancelAfterPublication)
        {
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(expireAfterPublication?KernelError.SessionClosed:KernelError.ResponseNotPending,s.Kernel.RequestSessionCancellation(s.Caller,invocation).Error);
            Assert.Equal(expireAfterPublication?KernelError.SessionClosed:KernelError.ResponseNotPending,s.Kernel.RequestSessionCancellation(s.Caller,invocation).Error);
            if(!expireAfterPublication)Assert.Equal(ResponsePublicationStatus.Published,s.Kernel.SnapshotSessionInvocationConsequence(invocation)?.TerminalStatus);
            Assert.Equal(ResponsePublicationStatus.Published,raw.Status);
            Assert.True(responses.ReadConsumedPublication(original,raw));
            Assert.All(forwarded.Span.ToArray(),b=>Assert.Equal((byte)0x63,b));
            Assert.Equal(BudgetReservationState.Active,s.Kernel.QueryBudget(forwardedCharge.Reservation).Value!.State);
        }
        if(!expireAfterPublication)Assert.True(s.Kernel.CloseSession(s.Caller,s.Session).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Caller).IsSuccess);
        Assert.True(s.Kernel.FaultComponent(new("compute-session-test")).IsSuccess);
        if(copiedRaw)
        {
            PropertyInfo property =original.Completion.GetType().GetProperty("RawPublication")!;
            property.SetValue(original.Completion,raw with {});
            Assert.False(responses.ReadPublication(original).IsSuccess);
            Assert.False(responses.ReadConsumedPublication(original,raw));
            Assert.All(forwarded.Span.ToArray(),b=>Assert.Equal((byte)0x63,b));
            Assert.Equal(BudgetReservationState.Active,s.Kernel.QueryBudget(forwardedCharge.Reservation).Value!.State);
            property.SetValue(original.Completion,raw);
        }
        Assert.Same(raw,responses.ReadPublication(original).Value);Assert.True(responses.ReadConsumedPublication(original,raw));
        Assert.All(forwarded.Span.ToArray(),b=>Assert.Equal((byte)0x63,b));
        Assert.Equal(BudgetReservationState.Active,s.Kernel.QueryBudget(forwardedCharge.Reservation).Value!.State);
        Assert.True(s.Kernel.ReleaseRegion(third,forwarded).IsSuccess);
        Assert.Equal(BudgetReservationState.Released,s.Kernel.QueryBudget(forwardedCharge.Reservation).Value!.State);
        Assert.Equal(RegionState.Released,s.Kernel.Regions.Snapshot().Single(r=>r.Handle.RegionId==originalHandle.RegionId).State);
        Assert.Same(raw,responses.ReadPublication(original).Value);Assert.True(responses.ReadConsumedPublication(original,raw));
        Assert.All(independent.Span.ToArray(),b=>Assert.Equal((byte)0x7D,b));
        Assert.True(s.Kernel.ReleaseRegion(third,independent).IsSuccess);Assert.True(s.Kernel.TerminateProcess(third).IsSuccess);
        Assert.Equal(1,spy.Copies);Assert.Equal(0,spy.Native.ActiveOwnedRegionMappingCount);
    }
}
