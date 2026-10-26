using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class ResponseOwnerContinuityTests
{
    [Theory]
    [InlineData(0,false)] [InlineData(1,false)] [InlineData(2,false)] [InlineData(3,false)]
    [InlineData(0,true)] [InlineData(1,true)] [InlineData(2,true)] [InlineData(3,true)]
    public void ClosedPollingReceiveRequiresOriginalRegistryChannel(int fault,bool waiter)
    {
        (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload, BudgetAccountHandle? Budget) x =Create(budgetRequester:true); (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload, BudgetAccountHandle? Budget) peer =Create(budgetRequester:true);
        x.Payload.Span.Fill(0x63);peer.Payload.Span.Fill(0x7D);
        Assert.True(x.Kernel.PublishResponse(x.Responder,x.Channel.Right,x.Sequence,x.Payload).IsSuccess);
        Assert.True(peer.Kernel.PublishResponse(peer.Responder,peer.Channel.Right,peer.Sequence,peer.Payload).IsSuccess);
        Assert.True(x.Kernel.Channels.Close(x.Channel.Left).IsSuccess);Assert.True(peer.Kernel.Channels.Close(peer.Channel.Left).IsSuccess);
        IDictionary Owned(RuntimeKernel kernel)
        {
            object registry =typeof(RuntimeKernel).GetProperty("Responses",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(kernel)!;
            return (IDictionary)registry.GetType().GetField("_ownedChannels",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(registry)!;
        }
        IDictionary routes =Owned(x.Kernel); object original =routes[x.Channel.Left.ChannelId]!;
        object other =Owned(peer.Kernel)[peer.Channel.Left.ChannelId]!;
                IDictionary Slots(object channel,string name)=>(IDictionary)channel.GetType().GetProperty(name)!.GetValue(channel)!;
        object originalCompletion =Slots(original,"OwnedCompletions")[x.Sequence]!;
        if(fault<2)routes[x.Channel.Left.ChannelId]=fault==1?other:typeof(object).GetMethod("MemberwiseClone",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(original,null)!;
        else
        {
            object replacement =fault==3?Slots(other,"OwnedCompletions")[peer.Sequence]!:typeof(object).GetMethod("MemberwiseClone",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(originalCompletion,null)!;
            Slots(original,"OwnedCompletions")[x.Sequence]=replacement;Slots(original,"Completions")[x.Sequence]=replacement;
        }
        KernelResult<ResponseEnvelope> refused =waiter ? x.Kernel.WaitForResponseAsync(x.Requester,x.Channel.Left,x.Sequence).GetAwaiter().GetResult() : x.Kernel.ReceiveResponse(x.Requester,x.Channel.Left);
        Assert.False(refused.IsSuccess);
        routes[x.Channel.Left.ChannelId]=original;
        Slots(original,"OwnedCompletions")[x.Sequence]=originalCompletion;Slots(original,"Completions")[x.Sequence]=originalCompletion;
        KernelResult<ResponseEnvelope> received =waiter ? x.Kernel.WaitForResponseAsync(x.Requester,x.Channel.Left,x.Sequence).GetAwaiter().GetResult() : x.Kernel.ReceiveResponse(x.Requester,x.Channel.Left);Assert.True(received.IsSuccess,received.Message);
        OwnedBuffer<byte> bytes =Assert.IsType<OwnedBuffer<byte>>(received.Value!.Payload);Assert.All(bytes.Span.ToArray(),b=>Assert.Equal((byte)0x63,b));
        Assert.True(x.Kernel.ReleaseRegion(x.Requester,bytes).IsSuccess);
        KernelResult<ResponseEnvelope> foreignResponse =peer.Kernel.ReceiveResponse(peer.Requester,peer.Channel.Left);Assert.True(foreignResponse.IsSuccess,foreignResponse.Message);
        OwnedBuffer<byte> foreignBytes =Assert.IsType<OwnedBuffer<byte>>(foreignResponse.Value!.Payload);Assert.All(foreignBytes.Span.ToArray(),b=>Assert.Equal((byte)0x7D,b));
        Assert.True(peer.Kernel.ReleaseRegion(peer.Requester,foreignBytes).IsSuccess);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget!.Value).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(0UL,peer.Kernel.QueryBudget(peer.Budget!.Value).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }
    [Fact]
    public async Task ClosedCorrelatedWaitConsumesExactSequenceAndLeavesOtherOriginalAvailable()
    {
        (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload, BudgetAccountHandle? Budget) x =Create();x.Payload.Span.Fill(0x63);
        OwnedBuffer<byte> independent =x.Kernel.AllocateBuffer<byte>(x.Responder,4).Value!;independent.Span.Fill(0x7D);
        ChannelEnvelope second =x.Kernel.Send(x.Requester,x.Responder,x.Channel.Left,1).Value!;
        Assert.True(x.Kernel.Receive(x.Responder,x.Channel.Right).IsSuccess);
        OwnedBuffer<byte> secondPayload =x.Kernel.AllocateBuffer<byte>(x.Responder,4).Value!;secondPayload.Span.Fill(0x42);
        ResponseRegistry responses =(ResponseRegistry)typeof(RuntimeKernel).GetProperty("Responses",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(x.Kernel)!;
        ResponseRegistry.OriginalPublication firstOriginal =responses.CapturePublication(x.Responder,x.Channel.Right,x.Sequence).Value!;
        ResponseRegistry.OriginalPublication secondOriginal =responses.CapturePublication(x.Responder,x.Channel.Right,second.Sequence).Value!;
        ResponseEnvelope firstRaw =x.Kernel.PublishResponse(x.Responder,x.Channel.Right,x.Sequence,x.Payload).Value!;
        ResponseEnvelope secondRaw =x.Kernel.PublishResponse(x.Responder,x.Channel.Right,second.Sequence,secondPayload).Value!;
        Assert.True(x.Kernel.Channels.Close(x.Channel.Left).IsSuccess);
        Assert.False((await x.Kernel.WaitForResponseAsync(x.Requester,x.Channel.Left,second.Sequence+1)).IsSuccess);
        Assert.False((await x.Kernel.WaitForResponseAsync(x.Responder,x.Channel.Left,second.Sequence)).IsSuccess);
        Assert.False((await x.Kernel.WaitForResponseAsync(x.Requester,x.Channel.Left with {Generation=x.Channel.Left.Generation+1},second.Sequence)).IsSuccess);
        KernelResult<ResponseEnvelope> secondReceived =await x.Kernel.WaitForResponseAsync(x.Requester,x.Channel.Left,second.Sequence);
        Assert.True(secondReceived.IsSuccess,secondReceived.Message);Assert.Same(secondRaw,secondReceived.Value);
        Assert.True(responses.ReadConsumedPublication(secondOriginal,secondRaw));
        Assert.False(responses.ReadConsumedPublication(firstOriginal,firstRaw));
        Assert.True(x.Kernel.ReleaseRegion(x.Requester,Assert.IsType<OwnedBuffer<byte>>(secondReceived.Value!.Payload)).IsSuccess);
        KernelResult<ResponseEnvelope> firstReceived =await x.Kernel.WaitForResponseAsync(x.Requester,x.Channel.Left,x.Sequence);
        Assert.True(firstReceived.IsSuccess,firstReceived.Message);Assert.Same(firstRaw,firstReceived.Value);
        OwnedBuffer<byte> firstBytes =Assert.IsType<OwnedBuffer<byte>>(firstReceived.Value!.Payload);Assert.All(firstBytes.Span.ToArray(),b=>Assert.Equal((byte)0x63,b));
        Assert.True(x.Kernel.ReleaseRegion(x.Requester,firstBytes).IsSuccess);
        Assert.True(responses.ReadConsumedPublication(firstOriginal,firstRaw));
        Assert.False((await x.Kernel.WaitForResponseAsync(x.Requester,x.Channel.Left,x.Sequence)).IsSuccess);
        Assert.All(independent.Span.ToArray(),b=>Assert.Equal((byte)0x7D,b));
        Assert.True(x.Kernel.ReleaseRegion(x.Responder,independent).IsSuccess);
    }
    [Fact]
    public void CommittedOwnershipResponseProvesExactOriginalInputProjectionWithoutRetransfer()
    {
        (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload, BudgetAccountHandle? Budget) x =Create(); (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload, BudgetAccountHandle? Budget) foreign =Create();
        ResponseRegistry responses =(ResponseRegistry)typeof(RuntimeKernel).GetProperty("Responses",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(x.Kernel)!;
        ResponseRegistry.OriginalPublication original =responses.CapturePublication(x.Responder,x.Channel.Right,x.Sequence).Value!;
        Assert.False(responses.ReadPublication(original).IsSuccess);
        KernelResult<ResponseEnvelope> published =x.Kernel.PublishResponse(x.Responder,x.Channel.Right,x.Sequence,x.Payload);Assert.True(published.IsSuccess);
        OwnedBuffer<byte> projection =Assert.IsType<OwnedBuffer<byte>>(published.Value!.Payload);
        Assert.False(x.Payload.IsValid);Assert.True(projection.IsValid);
        Assert.True(responses.MatchesOriginalPublicationPayload(original,x.Payload,published.Value));
        Assert.False(responses.MatchesOriginalPublicationPayload(original,foreign.Payload,published.Value));
        Assert.False(responses.MatchesOriginalPublicationPayload(original,projection,published.Value with {Payload=foreign.Payload}));
        Assert.True(x.Kernel.ReleaseRegion(x.Requester,projection).IsSuccess);
        Assert.True(x.Kernel.Channels.Close(x.Channel.Left).IsSuccess);
        Assert.Same(published.Value,responses.ReadPublication(original).Value);
        Assert.True(responses.MatchesOriginalPublicationPayload(original,x.Payload,published.Value));
        Assert.False(projection.IsValid);Assert.True(foreign.Payload.IsValid);
    }
    [Fact]
    public void ForeignOrCopiedPreparationCannotTransferAnotherOriginalRegion()
    {
        (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload, BudgetAccountHandle? Budget) x =Create(); OwnedBuffer<byte> other =x.Kernel.AllocateBuffer<byte>(x.Responder,4).Value!;
        ITransferableOwnedPayload source =(ITransferableOwnedPayload)x.Payload; PreparedOwnedTransfer preparation =source.PrepareTransferForRuntime();
        RegionOwner owner =new RegionOwner(new DomainId(912_100),x.Responder.Generation); RegionOwner target =new RegionOwner(new DomainId(912_000),x.Requester.Generation);
        KernelResult<RegionHandle> refused =x.Kernel.Regions.Transfer(other.Handle,owner,target,(ITransferableOwnedPayload)other,preparation);
        Assert.False(refused.IsSuccess);Assert.Equal(owner,x.Kernel.Regions.Snapshot().Single(r=>r.Handle==other.Handle).Owner);
        PreparedOwnedTransfer copy =(PreparedOwnedTransfer)typeof(object).GetMethod("MemberwiseClone",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(preparation,null)!;
        Assert.False(x.Kernel.Regions.Transfer(x.Payload.Handle,owner,target,source,copy).IsSuccess);
        Assert.Throws<InvalidOperationException>(()=>copy.Dispose());
        Assert.Throws<InvalidOperationException>(()=>{_=x.Payload.Span.Length;});
        Assert.Equal(owner,x.Kernel.Regions.Snapshot().Single(r=>r.Handle==x.Payload.Handle).Owner);
        Assert.False(((ITransferableOwnedPayload)preparation.Projection).IsValidForRuntime);
        preparation.Dispose();Assert.Equal(4,x.Payload.Span.Length);Assert.True(other.IsValid);
        Assert.True(x.Kernel.PublishResponse(x.Responder,x.Channel.Right,x.Sequence,x.Payload).IsSuccess);
    }
    [Fact]
    public void ResponseBudgetRefusalBeforeRegionEffectReleasesOnlyPreparedManagedCustody()
    {
        (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload, BudgetAccountHandle? Budget) x =Create(budgetRequester:true);
        OwnedBuffer<byte> independent =x.Kernel.AllocateBuffer<byte>(x.Requester,4).Value!;independent.Span.Fill(99);
        KernelResult<ResponseEnvelope> result =x.Kernel.PublishResponse(x.Responder,x.Channel.Right,x.Sequence,x.Payload);
        Assert.False(result.IsSuccess);
        Assert.True(x.Payload.IsValid);Assert.Equal(4,x.Payload.Span.Length);
        Assert.Equal(new RegionOwner(new DomainId(912_100),x.Responder.Generation),x.Kernel.Regions.Snapshot().Single(r=>r.Handle==x.Payload.Handle).Owner);
        Assert.All(independent.Span.ToArray(),b=>Assert.Equal((byte)99,b));
        Assert.Equal(4UL,x.Kernel.QueryBudget(x.Budget!.Value).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True(x.Kernel.ReleaseRegion(x.Requester,independent).IsSuccess);
        Assert.False(x.Kernel.PublishResponse(x.Responder,x.Channel.Right,x.Sequence,x.Payload).IsSuccess); // No implicit retry guarantee.
        Assert.Equal(4,x.Payload.Span.Length);
        Assert.True(x.Kernel.ReleaseRegion(x.Responder,x.Payload).IsSuccess);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget.Value).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EqualOwnershipWrapperIsNotTheRegisteredResponsePayload(bool foreignOriginal)
    {
        (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload, BudgetAccountHandle? Budget) x =Create(); OwnedBuffer<byte> copy =foreignOriginal ? Create().Payload :
            (OwnedBuffer<byte>)typeof(object).GetMethod("MemberwiseClone",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(x.Payload,null)!;
        Assert.Equal(x.Payload.Handle,copy.Handle);
        KernelResult<ResponseEnvelope> refused =x.Kernel.PublishResponse(x.Responder,x.Channel.Right,x.Sequence,copy);
        Assert.Equal(foreignOriginal ? KernelError.StaleHandle : KernelError.InvalidRegionState,refused.Error);
        if(foreignOriginal){Assert.True(copy.IsValid);Assert.Equal(4,copy.Span.Length);}
        Assert.True(x.Payload.IsValid);Assert.Equal(4,x.Payload.Span.Length);
        Assert.Equal(new RegionOwner(new DomainId(912_100),x.Responder.Generation),x.Kernel.Regions.Snapshot().Single(r=>r.Handle==x.Payload.Handle).Owner);
        Assert.Empty((IEnumerable)Routes(x.Kernel)[x.Channel.Left.ChannelId]!.GetType().GetProperty("LeftResponses")!.GetValue(Routes(x.Kernel)[x.Channel.Left.ChannelId])!);
    }
    [Fact]
    public void RegionTransferRefusesChangedOriginalPayloadBeforeOwnerOrGenerationMutation()
    {
        (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload, BudgetAccountHandle? Budget) x =Create(); RegionHandle handle =x.Payload.Handle; RegionOwner owner =new RegionOwner(new DomainId(912_100),x.Responder.Generation);
        KernelResult<ITransferableOwnedPayload> captured =x.Kernel.Regions.CaptureTransferPayload(handle,owner);Assert.True(captured.IsSuccess);
        OwnedBuffer<byte> replacement =x.Payload.Move();
        x.Kernel.Regions.ReplacePayload(handle,handle,(ITransferableOwnedPayload)replacement);
        KernelResult<RegionHandle> refused =x.Kernel.Regions.Transfer(handle,owner,new RegionOwner(new DomainId(912_000),x.Requester.Generation),captured.Value);
        Assert.Equal(KernelError.StaleHandle,refused.Error);
        Assert.True(replacement.IsValid);Assert.Equal(handle,replacement.Handle);
        Assert.Equal(owner,x.Kernel.Regions.Snapshot().Single(r=>r.Handle==handle).Owner);
        Assert.True(x.Kernel.PublishResponse(x.Responder,x.Channel.Right,x.Sequence,replacement).IsSuccess);
        Assert.False(replacement.IsValid);
    }
    [Fact]
    public void PreparedResponseCustodyBlocksCompetingMoveAndBorrowWithoutChangingRegionOwner()
    {
        (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload, BudgetAccountHandle? Budget) x =Create(); ITransferableOwnedPayload source =(ITransferableOwnedPayload)x.Payload;
        PreparedOwnedTransfer prepared =source.PrepareTransferForRuntime();
        Assert.False(((ITransferableOwnedPayload)prepared.Projection).IsValidForRuntime);
        Assert.Throws<InvalidOperationException>(()=>x.Payload.Move());
        Assert.Throws<InvalidOperationException>(()=>{_=x.Payload.Span.Length;});
        Assert.Equal(new RegionOwner(new DomainId(912_100),x.Responder.Generation),x.Kernel.Regions.Snapshot().Single(r=>r.Handle==x.Payload.Handle).Owner);
        prepared.Dispose();
        Assert.True(x.Payload.IsValid);Assert.Equal(4,x.Payload.Span.Length);
        Assert.False(((ITransferableOwnedPayload)prepared.Projection).IsValidForRuntime);
        Assert.Throws<InvalidOperationException>(()=>prepared.Commit(x.Payload.Handle));
        Assert.True(x.Kernel.PublishResponse(x.Responder,x.Channel.Right,x.Sequence,x.Payload).IsSuccess);
        Assert.False(x.Payload.IsValid);
    }
    [Fact]
    public void AlreadyFinalizedCompletionCannotTransferOwnershipOrQueueDuplicateResponse()
    {
        (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload, BudgetAccountHandle? Budget) x =Create(); object owner =Routes(x.Kernel)[x.Channel.Left.ChannelId]!;
        IDictionary completions =(IDictionary)owner.GetType().GetProperty("OwnedCompletions")!.GetValue(owner)!;
        object completion =completions[x.Sequence]!;
        TaskCompletionSource<ResponseEnvelope> source =(TaskCompletionSource<ResponseEnvelope>)completion.GetType().GetProperty("Source")!.GetValue(completion)!;
        source.SetResult(new(x.Sequence,1,ResponsePublicationStatus.Cancelled,null));
        KernelResult<ResponseEnvelope> published =x.Kernel.PublishResponse(x.Responder,x.Channel.Right,x.Sequence,x.Payload);
        Assert.False(published.IsSuccess);Assert.True(x.Payload.IsValid);
        Assert.Equal(new RegionOwner(new DomainId(912_100),x.Responder.Generation),x.Kernel.Regions.Snapshot().Single(r=>r.Handle==x.Payload.Handle).Owner);
        Assert.Empty((IEnumerable)owner.GetType().GetProperty("LeftResponses")!.GetValue(owner)!);
        Assert.Single((IDictionary)owner.GetType().GetProperty("Pending")!.GetValue(owner)!);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OriginalResponseDeliveryRefusesBeforeQueueConsumptionWhenRequestIndexIsLost(bool completionIndex)
    {
        (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload, BudgetAccountHandle? Budget) original = Create(deliver: false, budgetRequester: true);
        ulong MessagesUsed() => Assert.Single(original.Kernel.QueryBudget(original.Budget!.Value).Value!.Usage,
            usage => usage.Dimension == ServiceBudgetDimension.IpcMessages).Used;
        Assert.Equal(1UL, MessagesUsed());
        Task<KernelResult<ResponseEnvelope>> wait = original.Kernel.WaitForResponseAsync(original.Requester, original.Channel.Left, original.Sequence).AsTask();
        object owner = Routes(original.Kernel)[original.Channel.Left.ChannelId]!;
        IDictionary index = (IDictionary)owner.GetType().GetProperty(completionIndex ? "Completions" : "Pending")!.GetValue(owner)!;
        object fact = index[original.Sequence]!;
        index.Remove(original.Sequence);
        IDictionary requestRoutes = (IDictionary)typeof(ChannelRegistry).GetField("_channels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(original.Kernel.Channels)!;
        object requestOwner = requestRoutes[original.Channel.Left.ChannelId]!;
        Queue<ChannelEnvelope> queue = (Queue<ChannelEnvelope>)requestOwner.GetType().GetProperty("Queue")!.GetValue(requestOwner)!;
        Assert.False(original.Kernel.Receive(original.Responder, original.Channel.Right).IsSuccess);
        Assert.Equal(1UL, MessagesUsed());
        Assert.Single(queue);
        Assert.Equal(original.Sequence, queue.Peek().Sequence);
        Assert.False(wait.IsCompleted);
        index[original.Sequence] = fact;
        Assert.True(original.Kernel.Receive(original.Responder, original.Channel.Right).IsSuccess);
        Assert.Equal(0UL, MessagesUsed());
        Assert.Empty(queue);
        Assert.True(original.Kernel.PublishResponse(original.Responder, original.Channel.Right, original.Sequence, original.Payload).IsSuccess);
        Assert.Equal(ResponsePublicationStatus.Published, (await wait.WaitAsync(TimeSpan.FromSeconds(5))).Value!.Status);
        Assert.True(original.Kernel.Channels.Close(original.Channel.Right).IsSuccess);
    }

    [Fact]
    public async Task LostResponseIndicesDoNotEraseOriginalOutstandingCapacity()
    {
        (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload, BudgetAccountHandle? Budget) original = Create();
        Task<KernelResult<ResponseEnvelope>> firstWait = original.Kernel.WaitForResponseAsync(original.Requester, original.Channel.Left, original.Sequence).AsTask();
        object owner = Routes(original.Kernel)[original.Channel.Left.ChannelId]!;
        ((IDictionary)owner.GetType().GetProperty("Pending")!.GetValue(owner)!).Clear();
        ((IDictionary)owner.GetType().GetProperty("Completions")!.GetValue(owner)!).Clear();
        KernelResult<ChannelEnvelope> second = original.Kernel.Send(original.Requester, original.Responder, original.Channel.Left, 1);
        Assert.True(second.IsSuccess);
        Task<KernelResult<ResponseEnvelope>> secondWait = original.Kernel.WaitForResponseAsync(original.Requester, original.Channel.Left, second.Value!.Sequence).AsTask();
        Assert.Equal(KernelError.CapacityExhausted, original.Kernel.Send(original.Requester, original.Responder, original.Channel.Left, 1).Error);
        Assert.True(original.Kernel.TerminateProcess(original.Responder).IsSuccess);
        Assert.Equal(ResponsePublicationStatus.Cancelled, (await firstWait.WaitAsync(TimeSpan.FromSeconds(5))).Value!.Status);
        Assert.Equal(ResponsePublicationStatus.Cancelled, (await secondWait.WaitAsync(TimeSpan.FromSeconds(5))).Value!.Status);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExactCompletedResponseConsumptionSurvivesCompletionIndexLoss(bool foreignIndex)
    {
        (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload, BudgetAccountHandle? Budget) original = Create();
        (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload, BudgetAccountHandle? Budget) foreign = Create();
        Task<KernelResult<ResponseEnvelope>> wait = original.Kernel.WaitForResponseAsync(original.Requester, original.Channel.Left, original.Sequence).AsTask();
        object owner = Routes(original.Kernel)[original.Channel.Left.ChannelId]!;
        IDictionary index = (IDictionary)owner.GetType().GetProperty("Completions")!.GetValue(owner)!;
        object gate = typeof(RuntimeKernel).GetField("_requestResponseCorrelationGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(original.Kernel)!;
        lock (gate)
        {
            Assert.True(original.Kernel.PublishResponse(original.Responder, original.Channel.Right, original.Sequence, original.Payload).IsSuccess);
            if (foreignIndex)
            {
                object foreignOwner = Routes(foreign.Kernel)[foreign.Channel.Left.ChannelId]!;
                index[original.Sequence] = ((IDictionary)foreignOwner.GetType().GetProperty("Completions")!.GetValue(foreignOwner)!)[foreign.Sequence];
            }
            else index.Remove(original.Sequence);
        }
        KernelResult<ResponseEnvelope> result = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsSuccess);
        Assert.Equal(ResponsePublicationStatus.Published, result.Value!.Status);
        OwnedBuffer<byte> moved = Assert.IsType<OwnedBuffer<byte>>(result.Value.Payload);
        Assert.True(moved.IsValid);
        Assert.Empty((IDictionary)owner.GetType().GetProperty("OwnedCompletions")!.GetValue(owner)!);
        Assert.False(original.Kernel.PublishResponse(original.Responder, original.Channel.Right, original.Sequence, moved).IsSuccess);
        Assert.True(moved.IsValid);
        Assert.True(foreign.Payload.IsValid);
        Assert.True(original.Kernel.Channels.Close(original.Channel.Right).IsSuccess);
        Assert.True(foreign.Kernel.Channels.Close(foreign.Channel.Right).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void BufferedResponseRequiresExactOriginalPublishedFact(bool foreignPayload)
    {
        (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload, BudgetAccountHandle? Budget) original = Create();
        (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload, BudgetAccountHandle? Budget) foreign = Create();
        Assert.True(original.Kernel.PublishResponse(original.Responder, original.Channel.Right, original.Sequence, original.Payload).IsSuccess);
        object owner = Routes(original.Kernel)[original.Channel.Left.ChannelId]!;
        Queue<ResponseEnvelope> queue = (Queue<ResponseEnvelope>)owner.GetType().GetProperty("LeftResponses")!.GetValue(owner)!;
        ResponseEnvelope exact = queue.Dequeue();
        queue.Enqueue(foreignPayload ? exact with { Payload = foreign.Payload } : exact with { });
        Assert.False(original.Kernel.ReceiveResponse(original.Requester, original.Channel.Left).IsSuccess);
        Assert.Single(queue);
        Assert.True(foreign.Payload.IsValid);
        queue.Clear();
        queue.Enqueue(exact);
        KernelResult<ResponseEnvelope> consumed = original.Kernel.ReceiveResponse(original.Requester, original.Channel.Left);
        Assert.True(consumed.IsSuccess);
        Assert.Same(exact, consumed.Value);
        Assert.True(Assert.IsType<OwnedBuffer<byte>>(consumed.Value!.Payload).IsValid);
        Assert.Empty((IDictionary)owner.GetType().GetProperty("OwnedCompletions")!.GetValue(owner)!);
        Assert.True(original.Kernel.Channels.Close(original.Channel.Right).IsSuccess);
        Assert.True(foreign.Kernel.Channels.Close(foreign.Channel.Right).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public async Task OriginalResponseRequestFactsControlRegionEffectAndWaiterClose(int fault)
    {
        (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload, BudgetAccountHandle? Budget) original = Create();
        (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload, BudgetAccountHandle? Budget) foreign = Create();
        Task<KernelResult<ResponseEnvelope>> originalWait = original.Kernel.WaitForResponseAsync(original.Requester, original.Channel.Left, original.Sequence).AsTask();
        Task<KernelResult<ResponseEnvelope>> foreignWait = foreign.Kernel.WaitForResponseAsync(foreign.Requester, foreign.Channel.Left, foreign.Sequence).AsTask();
        object owner = Routes(original.Kernel)[original.Channel.Left.ChannelId]!;
        object foreignOwner = Routes(foreign.Kernel)[foreign.Channel.Left.ChannelId]!;
        IDictionary Index(object record, string name) => (IDictionary)record.GetType().GetProperty(name)!.GetValue(record)!;
        IDictionary pending = Index(owner, "Pending");
        IDictionary completions = Index(owner, "Completions");
        IDictionary index = fault < 3 ? pending : completions;
        object originalFact = index[original.Sequence]!;
        if (fault is 0 or 3 or 6) index.Remove(original.Sequence);
        else index[original.Sequence] = fault is 1 or 4
            ? Index(foreignOwner, fault < 3 ? "Pending" : "Completions")[foreign.Sequence]
            : originalFact.GetType().GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(originalFact, null);
        if (fault == 6) pending.Remove(original.Sequence);
        Assert.False(original.Kernel.PublishResponse(original.Responder, original.Channel.Right, original.Sequence, original.Payload).IsSuccess);
        Assert.True(original.Payload.IsValid);
        Assert.True(original.Kernel.Regions.Validate(original.Payload.Handle, new RegionOwner(new DomainId(912_100), original.Responder.Generation)).IsSuccess);
        Assert.False(originalWait.IsCompleted);
        Assert.False(foreignWait.IsCompleted);
        Assert.True(original.Kernel.TerminateProcess(original.Responder).IsSuccess);
        KernelResult<ResponseEnvelope> cancelled = await originalWait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cancelled.IsSuccess);
        Assert.Equal(ResponsePublicationStatus.Cancelled, cancelled.Value!.Status);
        Assert.False(foreignWait.IsCompleted);
        Assert.True(foreign.Payload.IsValid);
        Assert.True(foreign.Kernel.Channels.Close(foreign.Channel.Right).IsSuccess);
        Assert.Equal(ResponsePublicationStatus.Cancelled, (await foreignWait.WaitAsync(TimeSpan.FromSeconds(5))).Value!.Status);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OriginalPublishedResponseIsConsumedAfterRouteLossWithoutFreshAdmission(bool foreignRoute)
    {
        (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload, BudgetAccountHandle? Budget) original = Create();
        (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload, BudgetAccountHandle? Budget) foreign = Create();
        Task<KernelResult<ResponseEnvelope>> wait = original.Kernel.WaitForResponseAsync(original.Requester, original.Channel.Left, original.Sequence).AsTask();
        IDictionary routes = Routes(original.Kernel);
        object owner = routes[original.Channel.Left.ChannelId]!;
        object gate = typeof(RuntimeKernel).GetField("_requestResponseCorrelationGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(original.Kernel)!;
        lock (gate)
        {
            Assert.True(original.Kernel.PublishResponse(original.Responder, original.Channel.Right, original.Sequence, original.Payload).IsSuccess);
            if (foreignRoute) routes[original.Channel.Left.ChannelId] = Routes(foreign.Kernel)[foreign.Channel.Left.ChannelId];
            else routes.Remove(original.Channel.Left.ChannelId);
        }
        KernelResult<ResponseEnvelope> response = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(response.IsSuccess);
        Assert.Equal(ResponsePublicationStatus.Published, response.Value!.Status);
        Assert.False(original.Payload.IsValid);
        OwnedBuffer<byte> moved = Assert.IsType<OwnedBuffer<byte>>(response.Value.Payload);
        Assert.True(moved.IsValid);
        Assert.True(original.Kernel.Regions.Validate(moved.Handle, new RegionOwner(new DomainId(912_000), original.Requester.Generation)).IsSuccess);
        Assert.Empty((IDictionary)owner.GetType().GetProperty("Completions")!.GetValue(owner)!);
        Assert.False(original.Kernel.PublishResponse(original.Responder, original.Channel.Right, original.Sequence, moved).IsSuccess);
        Assert.True(moved.IsValid);
        Assert.True(original.Kernel.Channels.Close(original.Channel.Right).IsSuccess);
        Assert.True(foreign.Payload.IsValid);
        Assert.True(foreign.Kernel.Channels.Close(foreign.Channel.Right).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task OriginalResponseOwnerControlsTransferPublicationAndWaiterClose(int routeFault)
    {
        (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload, BudgetAccountHandle? Budget) original = Create();
        (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload, BudgetAccountHandle? Budget) foreign = Create();
        Assert.Equal(original.Channel.Left, foreign.Channel.Left);
        Assert.Equal(original.Sequence, foreign.Sequence);
        Task<KernelResult<ResponseEnvelope>> originalWait = original.Kernel.WaitForResponseAsync(original.Requester, original.Channel.Left, original.Sequence).AsTask();
        Task<KernelResult<ResponseEnvelope>> foreignWait = foreign.Kernel.WaitForResponseAsync(foreign.Requester, foreign.Channel.Left, foreign.Sequence).AsTask();
        IDictionary routes = Routes(original.Kernel);
        object originalOwner = routes[original.Channel.Left.ChannelId]!;
        if (routeFault == 0) routes.Remove(original.Channel.Left.ChannelId);
        else routes[original.Channel.Left.ChannelId] = routeFault == 1
            ? Routes(foreign.Kernel)[foreign.Channel.Left.ChannelId]
            : originalOwner.GetType().GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(originalOwner, null);
        RegionHandle handle = original.Payload.Handle;
        Assert.False(original.Kernel.PublishResponse(original.Responder, original.Channel.Right, original.Sequence, original.Payload).IsSuccess);
        Assert.True(original.Payload.IsValid);
        Assert.Equal(handle, original.Payload.Handle);
        Assert.True(original.Kernel.Regions.Validate(handle, new RegionOwner(new DomainId(912_100), original.Responder.Generation)).IsSuccess);
        Assert.False(originalWait.IsCompleted);
        Assert.False(foreignWait.IsCompleted);
        Assert.True(original.Kernel.TerminateProcess(original.Responder).IsSuccess);
        KernelResult<ResponseEnvelope> cancelled = await originalWait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cancelled.IsSuccess);
        Assert.Equal(ResponsePublicationStatus.Cancelled, cancelled.Value!.Status);
        Assert.False(foreignWait.IsCompleted);
        Assert.True(foreign.Payload.IsValid);
        Assert.True(foreign.Kernel.Channels.Close(foreign.Channel.Right).IsSuccess);
        Assert.Equal(ResponsePublicationStatus.Cancelled, (await foreignWait.WaitAsync(TimeSpan.FromSeconds(5))).Value!.Status);
    }

    private static IDictionary Routes(RuntimeKernel kernel)
    {
        object registry = typeof(RuntimeKernel).GetProperty("Responses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
        return (IDictionary)registry.GetType().GetField("_channels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
    }

    private static (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder,
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload,
        BudgetAccountHandle? Budget) Create(bool deliver = true, bool budgetRequester = false)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle requester;
        BudgetAccountHandle? budget = null;
        if (budgetRequester)
        {
            byte[] image = [1, 2];
            ServiceManifestV1 manifest = new ServiceManifestV1(new("response-delivery-budget"), new("1"),
                Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(image)).ToLowerInvariant(),
                TestFixtures.Manifest(9120, 912_000), budgetRequests:
                [new(ServiceBudgetDimension.IpcMessages, 2), new(ServiceBudgetDimension.IpcBytes, 64),
                 new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]);
            KernelResult<ComponentLifecycleSnapshot> admitted = kernel.AdmitComponent(new(manifest, image));
            Assert.True(admitted.IsSuccess, admitted.Message);
            requester = admitted.Value!.Process;
            budget = admitted.Value.ProcessBudget;
        }
        else requester = TestFixtures.Create(kernel, 9120, 912_000).Handle;
        ProcessHandle responder = TestFixtures.Create(kernel, 9121, 912_100).Handle;
        ProtocolDefinitionV1 protocol = new ProtocolDefinitionV1("OriginalResponseOwner", "owner-digest", "Ready", null,
            [new(1, "Return", returnsOwnership: true, returnOwnershipPayloadKind: OwnershipPayloadKind.OwnedBuffer)],
            [new(1, "Ready", "Ready")]);
        ResponseProtocolDefinitionV1 responses = new ResponseProtocolDefinitionV1(protocol.ContractName, protocol.ContractDigest,
            [new(1, "Return", new(ResponsePayloadKind.Ownership, typeof(OwnedBuffer<>).Namespace + ".OwnedBuffer", ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer))]);
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) channel = kernel.CreateChannel(requester, responder, protocol, responses, 2).Value!;
        ChannelEnvelope sent = kernel.Send(requester, responder, channel.Left, 1).Value!;
        if (deliver) Assert.True(kernel.Receive(responder, channel.Right).IsSuccess);
        return (kernel, requester, responder, channel, sent.Sequence, kernel.AllocateBuffer<byte>(responder, 4).Value!, budget);
    }
}

