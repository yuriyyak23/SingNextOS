using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip.Compute;
using SpyTypes=SingPlus.Tests.Platform.PlatformDsc1ComputeTests;

namespace SingPlus.Tests.Contracts;
public sealed partial class ComputeServiceSessionMigrationTests
{
    private sealed class PublishedCloseClock : TimeProvider
    {
        internal Action? Observe;
        internal DateTimeOffset? UtcNow;
        internal bool ThrowUtc;
        internal Action? ObserveUtc;
        public override DateTimeOffset GetUtcNow(){ObserveUtc?.Invoke();return ThrowUtc?throw new IOException("Expiry clock unavailable"):UtcNow??base.GetUtcNow();}
        public override long GetTimestamp(){Observe?.Invoke();return base.GetTimestamp();}
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RegisteredOwnershipWaiterRetainsOriginalForRetryAfterProjectionDrift(bool close)
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var runtime=DispatchProxy.Create<SpyTypes.IOriginalDsc1Runtime,SpyTypes.Dsc1NativeSpy>();
        var spy=(SpyTypes.Dsc1NativeSpy)(object)runtime;spy.Native=new();
        var s=CreateComponentScenario(new HybridCpuPlatformAuthorityProvider(runtime));s.Source.Span.Fill(0x63);
        var foreign=s.Kernel.AllocateBuffer<byte>(s.Service,16).Value!;foreign.Span.Fill(0x7D);
        var endpoint=s.Kernel.ResolveSession(s.Caller,s.Session).Value!.Channel;
        var reply=ComputeServiceClient.Create(new RuntimeSipClientTransport(s.Kernel,s.Caller,s.Session)).CopyAsync(s.Source,s.Destination).AsTask();
        ResponseRegistry.OriginalPublication original;ResponseEnvelope raw;SingPlus.Sip.OwnedBuffer<byte> projection;
        var correlation=typeof(RuntimeKernel).GetField("_requestResponseCorrelationGate",flags)!.GetValue(s.Kernel)!;
        lock(correlation)
        {
            Assert.True(s.Host.ProcessNextCopy().IsSuccess);
            var registry=typeof(RuntimeKernel).GetProperty("SessionInvocations",flags)!.GetValue(s.Kernel)!;
            var records=(global::System.Collections.IDictionary)registry.GetType().GetField("_ownedRecords",flags)!.GetValue(registry)!;
            var record=records[(s.Session.SessionId,s.Session.Generation,new EndpointSessionInvocationId(1))]!;
            original=(ResponseRegistry.OriginalPublication)record.GetType().GetProperty("OriginalResponse")!.GetValue(record)!;
            raw=original.Owner.ReadPublication(original).Value!;projection=Assert.IsType<SingPlus.Sip.OwnedBuffer<byte>>(raw.Payload);
            typeof(ResponseEnvelope).GetProperty("Payload")!.SetValue(raw,foreign);
            if(close)Assert.True(s.Kernel.Channels.Close(endpoint).IsSuccess);
        }
        await Assert.ThrowsAnyAsync<InvalidOperationException>(()=>reply);
        Assert.Equal(1,spy.Copies);Assert.False(original.Owner.ReadConsumedPublication(original,raw));
        var charge=Assert.Single(s.Kernel.Budgets.OriginalRegionCharges(projection.Handle));
        Assert.Equal(BudgetReservationState.Active,s.Kernel.QueryBudget(charge.Reservation).Value!.State);
        Assert.False((await s.Kernel.WaitForResponseAsync(s.Caller,endpoint,1)).IsSuccess);
        typeof(ResponseEnvelope).GetProperty("Payload")!.SetValue(raw,projection);
        var retry=await s.Kernel.WaitForResponseAsync(s.Caller,endpoint,1);
        Assert.True(retry.IsSuccess,retry.Message);Assert.Same(raw,retry.Value);Assert.Same(projection,retry.Value!.Payload);
        Assert.True(original.Owner.ReadConsumedPublication(original,raw));Assert.Equal(1,spy.Copies);
        Assert.All(projection.Span.ToArray(),b=>Assert.Equal((byte)0x63,b));
        Assert.True(s.Kernel.ReleaseRegion(s.Caller,projection).IsSuccess);
        Assert.Equal(BudgetReservationState.Released,s.Kernel.QueryBudget(charge.Reservation).Value!.State);
        Assert.All(foreign.Span.ToArray(),b=>Assert.Equal((byte)0x7D,b));Assert.True(s.Kernel.ReleaseRegion(s.Service,foreign).IsSuccess);
        Assert.True(s.Kernel.FaultComponent(new("compute-session-test")).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Caller).IsSuccess);Assert.Equal(0,spy.Native.ActiveOwnedRegionMappingCount);
    }
    [Theory]
    [InlineData("raw-copy")] [InlineData("raw-foreign")] [InlineData("prepared-copy")] [InlineData("prepared-absent")]
    public async Task DeferredOwnershipConsumptionRequiresOriginalPreparedProjection(string fault)
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        object Clone(object value)=>typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(value,null)!;
        var runtime=DispatchProxy.Create<SpyTypes.IOriginalDsc1Runtime,SpyTypes.Dsc1NativeSpy>();
        var spy=(SpyTypes.Dsc1NativeSpy)(object)runtime;spy.Native=new();
        var clock=new PublishedCloseClock();var s=CreateComponentScenario(new HybridCpuPlatformAuthorityProvider(runtime),clock);
        s.Source.Span.Fill(0x63);
        var foreign=s.Kernel.AllocateBuffer<byte>(s.Service,16).Value!;foreign.Span.Fill(0x7D);
        var endpoint=s.Kernel.ResolveSession(s.Caller,s.Session).Value!.Channel;
        Assert.True(s.Kernel.StartTraceSession(s.Caller,32).IsSuccess);
        var invocation=new EndpointSessionInvocationHandle(s.Session,new(1),new(1));
        ResponseRegistry.OriginalPublication? original=null;ResponseEnvelope? raw=null;
        object? prepared=null;SingPlus.Sip.OwnedBuffer<byte>? projection=null;var callbacks=0;
        clock.Observe=()=>
        {
            if(s.Kernel.SnapshotSessionInvocationConsequence(invocation) is null)return;
            clock.Observe=null;callbacks++;
            Assert.True(s.Host.ProcessNextCopy().IsSuccess);
            var registry=typeof(RuntimeKernel).GetProperty("SessionInvocations",flags)!.GetValue(s.Kernel)!;
            var records=(global::System.Collections.IDictionary)registry.GetType().GetField("_ownedRecords",flags)!.GetValue(registry)!;
            var record=records[(s.Session.SessionId,s.Session.Generation,new EndpointSessionInvocationId(1))]!;
            original=(ResponseRegistry.OriginalPublication)record.GetType().GetProperty("OriginalResponse")!.GetValue(record)!;
            raw=original.Owner.ReadPublication(original).Value!;projection=Assert.IsType<SingPlus.Sip.OwnedBuffer<byte>>(raw.Payload);
            var property=original.Completion.GetType().GetProperty("PreparedOwnership")!;prepared=property.GetValue(original.Completion)!;
            Assert.True(s.Kernel.Channels.Close(endpoint).IsSuccess);
            if(fault.StartsWith("raw-",StringComparison.Ordinal))typeof(ResponseEnvelope).GetProperty("Payload")!.SetValue(raw,fault=="raw-copy"?Clone(projection):foreign);
            else property.SetValue(original.Completion,fault=="prepared-copy"?Clone(prepared):null);
        };
        var reply=ComputeServiceClient.Create(new RuntimeSipClientTransport(s.Kernel,s.Caller,s.Session)).CopyAsync(s.Source,s.Destination).AsTask();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(()=>reply);
        Assert.Equal(1,callbacks);Assert.Equal(1,spy.Copies);Assert.NotNull(original);Assert.NotNull(projection);Assert.NotNull(raw);
        Assert.False(original.Owner.ReadConsumedPublication(original,raw));
        Assert.False(original.Owner.ReadPublication(original).IsSuccess);
        var charge=Assert.Single(s.Kernel.Budgets.OriginalRegionCharges(projection.Handle));
        Assert.Equal(BudgetReservationState.Active,s.Kernel.QueryBudget(charge.Reservation).Value!.State);
        Assert.True(s.Kernel.Regions.Validate(projection.Handle,new(new(9220),s.Caller.Generation)).IsSuccess);
        Assert.All(foreign.Span.ToArray(),b=>Assert.Equal((byte)0x7D,b));
        typeof(ResponseEnvelope).GetProperty("Payload")!.SetValue(raw,projection);
        original.Completion.GetType().GetProperty("PreparedOwnership")!.SetValue(original.Completion,prepared);
        var recovered=await s.Kernel.WaitForResponseAsync(s.Caller,endpoint,1);
        Assert.True(recovered.IsSuccess,recovered.Message);Assert.Same(raw,recovered.Value);Assert.Same(projection,recovered.Value!.Payload);
        Assert.True(original.Owner.ReadConsumedPublication(original,raw));Assert.Equal(1,spy.Copies);
        Assert.All(projection.Span.ToArray(),b=>Assert.Equal((byte)0x63,b));
        Assert.True(s.Kernel.ReleaseRegion(s.Caller,projection).IsSuccess);
        Assert.Equal(BudgetReservationState.Released,s.Kernel.QueryBudget(charge.Reservation).Value!.State);
        Assert.All(foreign.Span.ToArray(),b=>Assert.Equal((byte)0x7D,b));Assert.True(s.Kernel.ReleaseRegion(s.Service,foreign).IsSuccess);
        Assert.True(s.Kernel.FaultComponent(new("compute-session-test")).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Caller).IsSuccess);Assert.Equal(0,spy.Native.ActiveOwnedRegionMappingCount);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GeneratedOwnershipClientConsumesOriginalClosedResponseAfterSendObserver(bool interrupt)
    {
        var runtime=DispatchProxy.Create<SpyTypes.IOriginalDsc1Runtime,SpyTypes.Dsc1NativeSpy>();
        var spy=(SpyTypes.Dsc1NativeSpy)(object)runtime;spy.Native=new();
        var clock=new PublishedCloseClock();var s=CreateComponentScenario(new HybridCpuPlatformAuthorityProvider(runtime),clock);
        s.Source.Span.Fill(0x63);
        var foreign=s.Kernel.AllocateBuffer<byte>(s.Service,4).Value!;foreign.Span.Fill(0x7D);
        var endpoint=s.Kernel.ResolveSession(s.Caller,s.Session).Value!.Channel;
        Assert.True(s.Kernel.StartTraceSession(s.Caller,32).IsSuccess);
        var invocation=new EndpointSessionInvocationHandle(s.Session,new(1),new(1));
        ResponseRegistry.OriginalPublication? original=null;var callbacks=0;
        clock.Observe=()=>
        {
            if(s.Kernel.SnapshotSessionInvocationConsequence(invocation) is null)return;
            clock.Observe=null;callbacks++;
            const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
            Assert.False(Monitor.IsEntered(typeof(RuntimeKernel).GetField("_requestResponseCorrelationGate",flags)!.GetValue(s.Kernel)!));
            Assert.True(s.Host.ProcessNextCopy().IsSuccess);
            var registry=typeof(RuntimeKernel).GetProperty("SessionInvocations",flags)!.GetValue(s.Kernel)!;
            var records=(global::System.Collections.IDictionary)registry.GetType().GetField("_ownedRecords",flags)!.GetValue(registry)!;
            var record=records[(s.Session.SessionId,s.Session.Generation,new EndpointSessionInvocationId(1))]!;
            original=(ResponseRegistry.OriginalPublication)record.GetType().GetProperty("OriginalResponse")!.GetValue(record)!;
            Assert.True(s.Kernel.Channels.Close(endpoint).IsSuccess);
            if(interrupt)throw new IOException("Send observation interrupted after original response publication and channel close");
        };
        var output=await ComputeServiceClient.Create(new RuntimeSipClientTransport(s.Kernel,s.Caller,s.Session)).CopyAsync(s.Source,s.Destination);
        Assert.Equal(1,callbacks);Assert.Equal(1,spy.Copies);Assert.False(s.Host.HasPendingCopy);
        Assert.NotNull(original);
        var raw=original.Owner.ReadPublication(original).Value!;
        Assert.Same(raw.Payload,output);Assert.True(original.Owner.ReadConsumedPublication(original,raw));
        Assert.All(output.Span.ToArray(),b=>Assert.Equal((byte)0x63,b));
        Assert.All(s.Source.Span.ToArray(),b=>Assert.Equal((byte)0x63,b));
        Assert.False(s.Kernel.WaitForResponseAsync(s.Caller,endpoint,2).AsTask().Result.IsSuccess);
        Assert.False(s.Kernel.ReceiveResponse(s.Caller,endpoint).IsSuccess);
        var charge=Assert.Single(s.Kernel.Budgets.OriginalRegionCharges(output.Handle));
        Assert.Equal(BudgetReservationState.Active,s.Kernel.QueryBudget(charge.Reservation).Value!.State);
        Assert.True(s.Kernel.ReleaseRegion(s.Caller,output).IsSuccess);
        Assert.Equal(BudgetReservationState.Released,s.Kernel.QueryBudget(charge.Reservation).Value!.State);
        Assert.All(foreign.Span.ToArray(),b=>Assert.Equal((byte)0x7D,b));
        Assert.True(s.Kernel.ReleaseRegion(s.Service,foreign).IsSuccess);
        Assert.True(s.Kernel.FaultComponent(new("compute-session-test")).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Caller).IsSuccess);Assert.Equal(0,spy.Native.ActiveOwnedRegionMappingCount);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PublishedComputeResponseSurvivesCloseBeforeHostContainment(bool interrupt)
    {
        var runtime=DispatchProxy.Create<SpyTypes.IOriginalDsc1Runtime,SpyTypes.Dsc1NativeSpy>();
        var spy=(SpyTypes.Dsc1NativeSpy)(object)runtime;spy.Native=new();
        var clock=new PublishedCloseClock();var s=CreateComponentScenario(new HybridCpuPlatformAuthorityProvider(runtime),clock);
        s.Source.Span.Fill(0x63);
        Assert.True(s.Kernel.StartTraceSession(s.Caller,32).IsSuccess);
        var reply=ComputeServiceClient.Create(new RuntimeSipClientTransport(s.Kernel,s.Caller,s.Session)).CopyAsync(s.Source,s.Destination).AsTask();
        var invocation=new EndpointSessionInvocationHandle(s.Session,new EndpointSessionInvocationId(1),new EndpointSessionInvocationGeneration(1));
        var closed=false;
        clock.Observe=()=>
        {
            if(s.Kernel.SnapshotSessionInvocationConsequence(invocation)?.TerminalStatus!=ResponsePublicationStatus.Published)return;
            clock.Observe=null;closed=true;
            Assert.True(s.Host.HasPendingCopy);
            Assert.True(s.Kernel.CloseSession(s.Caller,s.Session).IsSuccess);
            if(interrupt)throw new IOException("Post-publication observer interrupted host return");
        };
        var first=s.Host.ProcessNextCopy();Assert.True(closed);Assert.True(first.IsSuccess,first.Message);
        var output=await reply.WaitAsync(TimeSpan.FromSeconds(2));Assert.All(output.Span.ToArray(),b=>Assert.Equal((byte)0x63,b));
        Assert.False(s.Host.HasPendingCopy);Assert.Equal(1,spy.Copies);
        var serviceBudget=s.Kernel.QueryComponent(new ComponentIdentity("compute-session-test")).Value!.ProcessBudget;
        Assert.Equal(0UL,s.Kernel.QueryBudget(serviceBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True(s.Kernel.ReleaseRegion(s.Caller,output).IsSuccess);
        Assert.True(s.Kernel.FaultComponent(new ComponentIdentity("compute-session-test")).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Caller).IsSuccess);Assert.Equal(0,spy.Native.ActiveOwnedRegionMappingCount);
    }
}

