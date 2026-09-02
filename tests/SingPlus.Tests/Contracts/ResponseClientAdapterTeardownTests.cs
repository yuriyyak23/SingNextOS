using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Contracts;

public sealed class ResponseClientAdapterTeardownTests
{
    [Fact]
    [Trait("Category", "Runtime")]
    public async Task RuntimeClientTransportWaitsForExactCorrelatedPublication()
    {
        var (kernel, requester, responder, endpoints) = CreateChannel();
        var transport = new RuntimeSipClientTransport(kernel, requester, responder, endpoints.Left);

        var pending = transport.InvokeAsync(1).AsTask();
        Assert.False(pending.IsCompleted);

        var request = kernel.Receive(responder, endpoints.Right);
        Assert.True(request.IsSuccess, request.Message);
        var publish = kernel.PublishResponse(
            responder,
            endpoints.Right,
            request.Value!.Sequence,
            42);
        Assert.True(publish.IsSuccess, publish.Message);

        var response = await pending;

        Assert.Equal(request.Value.Sequence, response.RequestSequence);
        Assert.Equal((uint)1, response.MessageId);
        Assert.Equal(ResponsePublicationStatus.Published, response.Status);
        Assert.Equal(42, Assert.IsType<int>(response.Payload));

        var duplicate = kernel.ReceiveResponse(requester, endpoints.Left);
        Assert.False(duplicate.IsSuccess);
        Assert.Equal(KernelError.ResponseNotAvailable, duplicate.Error);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public async Task DomainTerminationCancelsAllPendingRuntimeWaitersAndClosesChannel()
    {
        var (kernel, requester, responder, endpoints) = CreateChannel(capacity: 4);
        var transport = new RuntimeSipClientTransport(kernel, requester, responder, endpoints.Left);

        var first = transport.InvokeAsync(1).AsTask();
        var second = transport.InvokeAsync(1).AsTask();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        var terminate = kernel.TerminateProcess(responder);
        Assert.True(terminate.IsSuccess, terminate.Message);

        var firstResponse = await first;
        var secondResponse = await second;

        Assert.Equal(ResponsePublicationStatus.Cancelled, firstResponse.Status);
        Assert.Equal(ResponsePublicationStatus.Cancelled, secondResponse.Status);
        Assert.Null(firstResponse.Payload);
        Assert.Null(secondResponse.Payload);
        Assert.True(firstResponse.RequestSequence < secondResponse.RequestSequence);

        var staleRequesterEndpoint = kernel.Channels.GetEndpoint(endpoints.Left);
        Assert.False(staleRequesterEndpoint.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, staleRequesterEndpoint.Error);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public async Task ProcessTerminationClosesItsResponseChannelEvenWhenDomainRemainsActive()
    {
        var kernel = new RuntimeKernel();
        var (_, requester) = TestFixtures.Create(kernel, 411, 410);
        var (_, responder) = TestFixtures.Create(kernel, 412, 420);
        _ = TestFixtures.Create(kernel, 413, 420);
        var endpoints = CreateChannel(kernel, requester, responder, capacity: 2);
        var transport = new RuntimeSipClientTransport(kernel, requester, responder, endpoints.Left);

        var pending = transport.InvokeAsync(1).AsTask();
        Assert.False(pending.IsCompleted);

        var terminate = kernel.TerminateProcess(responder);
        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.True(kernel.Domains.Contains(new DomainId(420)));

        var response = await pending;
        Assert.Equal(ResponsePublicationStatus.Cancelled, response.Status);

        var stale = kernel.Channels.GetEndpoint(endpoints.Left);
        Assert.False(stale.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, stale.Error);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public async Task PublishedResponseRemainsCommittedWhenPeerTerminatesAfterPublication()
    {
        var (kernel, requester, responder, endpoints) = CreateChannel();
        var transport = new RuntimeSipClientTransport(kernel, requester, responder, endpoints.Left);

        var pending = transport.InvokeAsync(1).AsTask();
        var request = kernel.Receive(responder, endpoints.Right);
        Assert.True(request.IsSuccess, request.Message);
        Assert.True(kernel.PublishResponse(responder, endpoints.Right, request.Value!.Sequence, 7).IsSuccess);

        var terminate = kernel.TerminateProcess(responder);
        Assert.True(terminate.IsSuccess, terminate.Message);

        var response = await pending;
        Assert.Equal(ResponsePublicationStatus.Published, response.Status);
        Assert.Equal(7, Assert.IsType<int>(response.Payload));
    }

    private static (
        RuntimeKernel Kernel,
        ProcessHandle Requester,
        ProcessHandle Responder,
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Endpoints) CreateChannel(int capacity = 2)
    {
        var kernel = new RuntimeKernel();
        var (_, requester) = TestFixtures.Create(kernel, 401, 410);
        var (_, responder) = TestFixtures.Create(kernel, 402, 420);
        return (kernel, requester, responder, CreateChannel(kernel, requester, responder, capacity));
    }

    private static (ChannelEndpointHandle Left, ChannelEndpointHandle Right) CreateChannel(
        RuntimeKernel kernel,
        ProcessHandle requester,
        ProcessHandle responder,
        int capacity)
    {
        const string contractName = "SingPlus.Tests.IResponseClientAdapterProtocol";
        var protocol = new ProtocolDefinitionV1(
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
        var responseProtocol = new ResponseProtocolDefinitionV1(
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

        var channel = kernel.CreateChannel(
            requester,
            responder,
            protocol,
            responseProtocol,
            capacity);
        Assert.True(channel.IsSuccess, channel.Message);
        return channel.Value;
    }
}
