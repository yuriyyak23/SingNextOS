using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Contracts;

public sealed class ResponseClientAdapterTeardownTests
{
    [Fact]
    [Trait("Category", "Runtime")]
    public async Task RuntimeClientTransportWaitsForExactCorrelatedPublication()
    {
        (RuntimeKernel? kernel, ProcessHandle requester, ProcessHandle responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints) = CreateChannel();
        RuntimeSipClientTransport transport = new RuntimeSipClientTransport(kernel, requester, responder, endpoints.Left);

        Task<ResponseEnvelope> pending = transport.InvokeAsync(1).AsTask();
        Assert.False(pending.IsCompleted);

        KernelResult<ChannelEnvelope> request = kernel.Receive(responder, endpoints.Right);
        Assert.True(request.IsSuccess, request.Message);
        KernelResult<ResponseEnvelope> publish = kernel.PublishResponse(
            responder,
            endpoints.Right,
            request.Value!.Sequence,
            42);
        Assert.True(publish.IsSuccess, publish.Message);

        ResponseEnvelope response = await pending;

        Assert.Equal(request.Value.Sequence, response.RequestSequence);
        Assert.Equal((uint)1, response.MessageId);
        Assert.Equal(ResponsePublicationStatus.Published, response.Status);
        Assert.Equal(42, Assert.IsType<int>(response.Payload));

        KernelResult<ResponseEnvelope> duplicate = kernel.ReceiveResponse(requester, endpoints.Left);
        Assert.False(duplicate.IsSuccess);
        Assert.Equal(KernelError.ResponseNotAvailable, duplicate.Error);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public async Task DomainTerminationCancelsAllPendingRuntimeWaitersAndClosesChannel()
    {
        (RuntimeKernel? kernel, ProcessHandle requester, ProcessHandle responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints) = CreateChannel(capacity: 4);
        RuntimeSipClientTransport transport = new RuntimeSipClientTransport(kernel, requester, responder, endpoints.Left);

        Task<ResponseEnvelope> first = transport.InvokeAsync(1).AsTask();
        Task<ResponseEnvelope> second = transport.InvokeAsync(1).AsTask();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        KernelResult terminate = kernel.TerminateProcess(responder);
        Assert.True(terminate.IsSuccess, terminate.Message);

        ResponseEnvelope firstResponse = await first;
        ResponseEnvelope secondResponse = await second;

        Assert.Equal(ResponsePublicationStatus.Cancelled, firstResponse.Status);
        Assert.Equal(ResponsePublicationStatus.Cancelled, secondResponse.Status);
        Assert.Null(firstResponse.Payload);
        Assert.Null(secondResponse.Payload);
        Assert.True(firstResponse.RequestSequence < secondResponse.RequestSequence);

        KernelResult<ChannelEndpoint> staleRequesterEndpoint = kernel.Channels.GetEndpoint(endpoints.Left);
        Assert.False(staleRequesterEndpoint.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, staleRequesterEndpoint.Error);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public async Task ProcessTerminationClosesItsResponseChannelEvenWhenDomainRemainsActive()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (Sip.SingProcess _, ProcessHandle requester) = TestFixtures.Create(kernel, 411, 410);
        (Sip.SingProcess _, ProcessHandle responder) = TestFixtures.Create(kernel, 412, 420);
        _ = TestFixtures.Create(kernel, 413, 420);
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints = CreateChannel(kernel, requester, responder, capacity: 2);
        RuntimeSipClientTransport transport = new RuntimeSipClientTransport(kernel, requester, responder, endpoints.Left);

        Task<ResponseEnvelope> pending = transport.InvokeAsync(1).AsTask();
        Assert.False(pending.IsCompleted);

        KernelResult terminate = kernel.TerminateProcess(responder);
        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.True(kernel.Domains.Contains(new DomainId(420)));

        ResponseEnvelope response = await pending;
        Assert.Equal(ResponsePublicationStatus.Cancelled, response.Status);

        KernelResult<ChannelEndpoint> stale = kernel.Channels.GetEndpoint(endpoints.Left);
        Assert.False(stale.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, stale.Error);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public async Task PublishedResponseRemainsCommittedWhenPeerTerminatesAfterPublication()
    {
        (RuntimeKernel? kernel, ProcessHandle requester, ProcessHandle responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints) = CreateChannel();
        RuntimeSipClientTransport transport = new RuntimeSipClientTransport(kernel, requester, responder, endpoints.Left);

        Task<ResponseEnvelope> pending = transport.InvokeAsync(1).AsTask();
        KernelResult<ChannelEnvelope> request = kernel.Receive(responder, endpoints.Right);
        Assert.True(request.IsSuccess, request.Message);
        Assert.True(kernel.PublishResponse(responder, endpoints.Right, request.Value!.Sequence, 7).IsSuccess);

        KernelResult terminate = kernel.TerminateProcess(responder);
        Assert.True(terminate.IsSuccess, terminate.Message);

        ResponseEnvelope response = await pending;
        Assert.Equal(ResponsePublicationStatus.Published, response.Status);
        Assert.Equal(7, Assert.IsType<int>(response.Payload));
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public async Task ConcurrentCancellationAndTeardownFinalizeOneResponseAndInvalidateEndpoint()
    {
        for (int iteration = 0; iteration < 32; iteration++)
        {
            (RuntimeKernel? kernel, ProcessHandle requester, ProcessHandle responder, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints) = CreateChannel();
            RuntimeSipClientTransport transport = new RuntimeSipClientTransport(kernel, requester, responder, endpoints.Left);
            Task<ResponseEnvelope> pending = transport.InvokeAsync(1).AsTask();
            KernelResult<ChannelEnvelope> request = kernel.Receive(responder, endpoints.Right);
            Assert.True(request.IsSuccess, request.Message);

            TaskCompletionSource<bool> start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<KernelResult<ResponseEnvelope>> cancel = Task.Run(async () =>
            {
                await start.Task;
                return kernel.CancelResponse(responder, endpoints.Right, request.Value!.Sequence);
            });
            Task<KernelResult> teardown = Task.Run(async () =>
            {
                await start.Task;
                return kernel.TerminateProcess(responder);
            });
            start.SetResult(true);
            await Task.WhenAll(cancel, teardown).WaitAsync(TimeSpan.FromSeconds(5));

            KernelResult closed = await teardown;
            Assert.True(closed.IsSuccess, closed.Message);
            Assert.Equal(ResponsePublicationStatus.Cancelled, (await pending).Status);
            Assert.Equal(KernelError.StaleGeneration, kernel.Channels.GetEndpoint(endpoints.Left).Error);
            Assert.False(kernel.ReceiveResponse(requester, endpoints.Left).IsSuccess);
        }
    }

    private static (
        RuntimeKernel Kernel,
        ProcessHandle Requester,
        ProcessHandle Responder,
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Endpoints) CreateChannel(int capacity = 2)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (Sip.SingProcess _, ProcessHandle requester) = TestFixtures.Create(kernel, 401, 410);
        (Sip.SingProcess _, ProcessHandle responder) = TestFixtures.Create(kernel, 402, 420);
        return (kernel, requester, responder, CreateChannel(kernel, requester, responder, capacity));
    }

    private static (ChannelEndpointHandle Left, ChannelEndpointHandle Right) CreateChannel(
        RuntimeKernel kernel,
        ProcessHandle requester,
        ProcessHandle responder,
        int capacity)
    {
        const string contractName = "SingPlus.Tests.IResponseClientAdapterProtocol";
        ProtocolDefinitionV1 protocol = new ProtocolDefinitionV1(
            contractName,
            "response-client-request-digest",
            "Idle",
            terminalStates: null,
            messages: new[]
            {
                new ProtocolMessageDescriptorV1(1, "Read")
            },
            transitions: new[]
            {
                new ProtocolTransitionV1(1, "Idle", "Idle")
            });
        ResponseProtocolDefinitionV1 responseProtocol = new ResponseProtocolDefinitionV1(
            contractName,
            "response-client-response-digest",
            new[]
            {
                new ResponseMessageDescriptorV1(
                    1,
                    "Read",
                    new ResponsePayloadDescriptorV1(
                        ResponsePayloadKind.Primitive,
                        typeof(int).FullName!))
            });

        KernelResult<(ChannelEndpointHandle Left, ChannelEndpointHandle Right)> channel = kernel.CreateChannel(
            requester,
            responder,
            protocol,
            responseProtocol,
            capacity);
        Assert.True(channel.IsSuccess, channel.Message);
        return channel.Value;
    }
}
