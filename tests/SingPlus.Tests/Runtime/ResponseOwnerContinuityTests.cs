using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class ResponseOwnerContinuityTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OriginalResponseDeliveryRefusesBeforeQueueConsumptionWhenRequestIndexIsLost(bool completionIndex)
    {
        var original = Create(deliver: false, budgetRequester: true);
        ulong MessagesUsed() => Assert.Single(original.Kernel.QueryBudget(original.Budget!.Value).Value!.Usage,
            usage => usage.Dimension == ServiceBudgetDimension.IpcMessages).Used;
        Assert.Equal(1UL, MessagesUsed());
        var wait = original.Kernel.WaitForResponseAsync(original.Requester, original.Channel.Left, original.Sequence).AsTask();
        var owner = Routes(original.Kernel)[original.Channel.Left.ChannelId]!;
        var index = (IDictionary)owner.GetType().GetProperty(completionIndex ? "Completions" : "Pending")!.GetValue(owner)!;
        var fact = index[original.Sequence]!;
        index.Remove(original.Sequence);
        var requestRoutes = (IDictionary)typeof(ChannelRegistry).GetField("_channels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(original.Kernel.Channels)!;
        var requestOwner = requestRoutes[original.Channel.Left.ChannelId]!;
        var queue = (Queue<ChannelEnvelope>)requestOwner.GetType().GetProperty("Queue")!.GetValue(requestOwner)!;
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
        var original = Create();
        var firstWait = original.Kernel.WaitForResponseAsync(original.Requester, original.Channel.Left, original.Sequence).AsTask();
        var owner = Routes(original.Kernel)[original.Channel.Left.ChannelId]!;
        ((IDictionary)owner.GetType().GetProperty("Pending")!.GetValue(owner)!).Clear();
        ((IDictionary)owner.GetType().GetProperty("Completions")!.GetValue(owner)!).Clear();
        var second = original.Kernel.Send(original.Requester, original.Responder, original.Channel.Left, 1);
        Assert.True(second.IsSuccess);
        var secondWait = original.Kernel.WaitForResponseAsync(original.Requester, original.Channel.Left, second.Value!.Sequence).AsTask();
        Assert.Equal(KernelError.CapacityExhausted, original.Kernel.Send(original.Requester, original.Responder, original.Channel.Left, 1).Error);
        Assert.True(original.Kernel.TerminateProcess(original.Responder).IsSuccess);
        Assert.Equal(ResponsePublicationStatus.Cancelled, (await firstWait.WaitAsync(TimeSpan.FromSeconds(5))).Value!.Status);
        Assert.Equal(ResponsePublicationStatus.Cancelled, (await secondWait.WaitAsync(TimeSpan.FromSeconds(5))).Value!.Status);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExactCompletedResponseConsumptionSurvivesCompletionIndexLoss(bool foreignIndex)
    {
        var original = Create();
        var foreign = Create();
        var wait = original.Kernel.WaitForResponseAsync(original.Requester, original.Channel.Left, original.Sequence).AsTask();
        var owner = Routes(original.Kernel)[original.Channel.Left.ChannelId]!;
        var index = (IDictionary)owner.GetType().GetProperty("Completions")!.GetValue(owner)!;
        var gate = typeof(RuntimeKernel).GetField("_requestResponseCorrelationGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(original.Kernel)!;
        lock (gate)
        {
            Assert.True(original.Kernel.PublishResponse(original.Responder, original.Channel.Right, original.Sequence, original.Payload).IsSuccess);
            if (foreignIndex)
            {
                var foreignOwner = Routes(foreign.Kernel)[foreign.Channel.Left.ChannelId]!;
                index[original.Sequence] = ((IDictionary)foreignOwner.GetType().GetProperty("Completions")!.GetValue(foreignOwner)!)[foreign.Sequence];
            }
            else index.Remove(original.Sequence);
        }
        var result = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsSuccess);
        Assert.Equal(ResponsePublicationStatus.Published, result.Value!.Status);
        var moved = Assert.IsType<OwnedBuffer<byte>>(result.Value.Payload);
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
        var original = Create();
        var foreign = Create();
        Assert.True(original.Kernel.PublishResponse(original.Responder, original.Channel.Right, original.Sequence, original.Payload).IsSuccess);
        var owner = Routes(original.Kernel)[original.Channel.Left.ChannelId]!;
        var queue = (Queue<ResponseEnvelope>)owner.GetType().GetProperty("LeftResponses")!.GetValue(owner)!;
        var exact = queue.Dequeue();
        queue.Enqueue(foreignPayload ? exact with { Payload = foreign.Payload } : exact with { });
        Assert.False(original.Kernel.ReceiveResponse(original.Requester, original.Channel.Left).IsSuccess);
        Assert.Single(queue);
        Assert.True(foreign.Payload.IsValid);
        queue.Clear();
        queue.Enqueue(exact);
        var consumed = original.Kernel.ReceiveResponse(original.Requester, original.Channel.Left);
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
        var original = Create();
        var foreign = Create();
        var originalWait = original.Kernel.WaitForResponseAsync(original.Requester, original.Channel.Left, original.Sequence).AsTask();
        var foreignWait = foreign.Kernel.WaitForResponseAsync(foreign.Requester, foreign.Channel.Left, foreign.Sequence).AsTask();
        var owner = Routes(original.Kernel)[original.Channel.Left.ChannelId]!;
        var foreignOwner = Routes(foreign.Kernel)[foreign.Channel.Left.ChannelId]!;
        IDictionary Index(object record, string name) => (IDictionary)record.GetType().GetProperty(name)!.GetValue(record)!;
        var pending = Index(owner, "Pending");
        var completions = Index(owner, "Completions");
        var index = fault < 3 ? pending : completions;
        var originalFact = index[original.Sequence]!;
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
        var cancelled = await originalWait.WaitAsync(TimeSpan.FromSeconds(5));
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
        var original = Create();
        var foreign = Create();
        var wait = original.Kernel.WaitForResponseAsync(original.Requester, original.Channel.Left, original.Sequence).AsTask();
        var routes = Routes(original.Kernel);
        var owner = routes[original.Channel.Left.ChannelId]!;
        var gate = typeof(RuntimeKernel).GetField("_requestResponseCorrelationGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(original.Kernel)!;
        lock (gate)
        {
            Assert.True(original.Kernel.PublishResponse(original.Responder, original.Channel.Right, original.Sequence, original.Payload).IsSuccess);
            if (foreignRoute) routes[original.Channel.Left.ChannelId] = Routes(foreign.Kernel)[foreign.Channel.Left.ChannelId];
            else routes.Remove(original.Channel.Left.ChannelId);
        }
        var response = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(response.IsSuccess);
        Assert.Equal(ResponsePublicationStatus.Published, response.Value!.Status);
        Assert.False(original.Payload.IsValid);
        var moved = Assert.IsType<OwnedBuffer<byte>>(response.Value.Payload);
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
        var original = Create();
        var foreign = Create();
        Assert.Equal(original.Channel.Left, foreign.Channel.Left);
        Assert.Equal(original.Sequence, foreign.Sequence);
        var originalWait = original.Kernel.WaitForResponseAsync(original.Requester, original.Channel.Left, original.Sequence).AsTask();
        var foreignWait = foreign.Kernel.WaitForResponseAsync(foreign.Requester, foreign.Channel.Left, foreign.Sequence).AsTask();
        var routes = Routes(original.Kernel);
        var originalOwner = routes[original.Channel.Left.ChannelId]!;
        if (routeFault == 0) routes.Remove(original.Channel.Left.ChannelId);
        else routes[original.Channel.Left.ChannelId] = routeFault == 1
            ? Routes(foreign.Kernel)[foreign.Channel.Left.ChannelId]
            : originalOwner.GetType().GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(originalOwner, null);
        var handle = original.Payload.Handle;
        Assert.False(original.Kernel.PublishResponse(original.Responder, original.Channel.Right, original.Sequence, original.Payload).IsSuccess);
        Assert.True(original.Payload.IsValid);
        Assert.Equal(handle, original.Payload.Handle);
        Assert.True(original.Kernel.Regions.Validate(handle, new RegionOwner(new DomainId(912_100), original.Responder.Generation)).IsSuccess);
        Assert.False(originalWait.IsCompleted);
        Assert.False(foreignWait.IsCompleted);
        Assert.True(original.Kernel.TerminateProcess(original.Responder).IsSuccess);
        var cancelled = await originalWait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cancelled.IsSuccess);
        Assert.Equal(ResponsePublicationStatus.Cancelled, cancelled.Value!.Status);
        Assert.False(foreignWait.IsCompleted);
        Assert.True(foreign.Payload.IsValid);
        Assert.True(foreign.Kernel.Channels.Close(foreign.Channel.Right).IsSuccess);
        Assert.Equal(ResponsePublicationStatus.Cancelled, (await foreignWait.WaitAsync(TimeSpan.FromSeconds(5))).Value!.Status);
    }

    private static IDictionary Routes(RuntimeKernel kernel)
    {
        var registry = typeof(RuntimeKernel).GetProperty("Responses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
        return (IDictionary)registry.GetType().GetField("_channels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
    }

    private static (RuntimeKernel Kernel, ProcessHandle Requester, ProcessHandle Responder,
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ulong Sequence, OwnedBuffer<byte> Payload,
        BudgetAccountHandle? Budget) Create(bool deliver = true, bool budgetRequester = false)
    {
        var kernel = new RuntimeKernel();
        ProcessHandle requester;
        BudgetAccountHandle? budget = null;
        if (budgetRequester)
        {
            byte[] image = [1, 2];
            var manifest = new ServiceManifestV1(new("response-delivery-budget"), new("1"),
                Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(image)).ToLowerInvariant(),
                TestFixtures.Manifest(9120, 912_000), budgetRequests:
                [new(ServiceBudgetDimension.IpcMessages, 2), new(ServiceBudgetDimension.IpcBytes, 64),
                 new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]);
            var admitted = kernel.AdmitComponent(new(manifest, image));
            Assert.True(admitted.IsSuccess, admitted.Message);
            requester = admitted.Value!.Process;
            budget = admitted.Value.ProcessBudget;
        }
        else requester = TestFixtures.Create(kernel, 9120, 912_000).Handle;
        var responder = TestFixtures.Create(kernel, 9121, 912_100).Handle;
        var protocol = new ProtocolDefinitionV1("OriginalResponseOwner", "owner-digest", "Ready", null,
            [new(1, "Return", returnsOwnership: true, returnOwnershipPayloadKind: OwnershipPayloadKind.OwnedBuffer)],
            [new(1, "Ready", "Ready")]);
        var responses = new ResponseProtocolDefinitionV1(protocol.ContractName, protocol.ContractDigest,
            [new(1, "Return", new(ResponsePayloadKind.Ownership, typeof(OwnedBuffer<>).Namespace + ".OwnedBuffer", ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer))]);
        var channel = kernel.CreateChannel(requester, responder, protocol, responses, 2).Value!;
        var sent = kernel.Send(requester, responder, channel.Left, 1).Value!;
        if (deliver) Assert.True(kernel.Receive(responder, channel.Right).IsSuccess);
        return (kernel, requester, responder, channel, sent.Sequence, kernel.AllocateBuffer<byte>(responder, 4).Value!, budget);
    }
}
