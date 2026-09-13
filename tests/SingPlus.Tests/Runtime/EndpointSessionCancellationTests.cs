using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class EndpointSessionCancellationTests
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CallerCancellationBeforeServiceAcceptanceSettlesAsCancelledWithoutExecutionAcceptance()
    {
        var scenario = CreateSession();
        using var cancellation = new CancellationTokenSource();
        var transport = new RuntimeSipClientTransport(
            scenario.Kernel,
            scenario.Caller,
            scenario.Session,
            cancellation.Token);
        var pending = transport.InvokeAsync(1).AsTask();

        cancellation.Cancel();
        var received = scenario.Kernel.ReceiveSessionRequest(scenario.Service, scenario.Session);

        Assert.True(received.IsSuccess, received.Message);
        Assert.True(received.Value!.CancellationRequested);
        Assert.Equal(
            KernelError.InvalidTransition,
            scenario.Kernel.AcceptSessionInvocation(
                scenario.Service,
                received.Value.Invocation,
                allowInFlightCancellation: false).Error);
        Assert.True(scenario.Kernel.AcceptSessionCancellation(
            scenario.Service,
            received.Value.Invocation).IsSuccess);
        Assert.True(scenario.Kernel.CancelSessionResponse(
            scenario.Service,
            received.Value.Invocation).IsSuccess);

        var response = await pending.WaitAsync(CompletionTimeout);
        Assert.Equal(ResponsePublicationStatus.Cancelled, response.Status);
        Assert.Null(response.Payload);
    }

    [Fact]
    public async Task ServiceAcceptanceWithoutInFlightCancellationMakesLateCallerCancellationLose()
    {
        var scenario = CreateSession();
        using var cancellation = new CancellationTokenSource();
        var transport = new RuntimeSipClientTransport(
            scenario.Kernel,
            scenario.Caller,
            scenario.Session,
            cancellation.Token);
        var pending = transport.InvokeAsync(1).AsTask();
        var received = scenario.Kernel.ReceiveSessionRequest(scenario.Service, scenario.Session);
        Assert.True(received.IsSuccess, received.Message);
        Assert.True(scenario.Kernel.AcceptSessionInvocation(
            scenario.Service,
            received.Value!.Invocation,
            allowInFlightCancellation: false).IsSuccess);

        cancellation.Cancel();

        var requested = scenario.Kernel.QuerySessionCancellation(
            scenario.Service,
            received.Value.Invocation);
        Assert.True(requested.IsSuccess, requested.Message);
        Assert.False(requested.Value);
        var published = scenario.Kernel.PublishSessionResponse(
            scenario.Service,
            received.Value.Invocation,
            42);
        Assert.True(published.IsSuccess, published.Message);

        var response = await pending.WaitAsync(CompletionTimeout);
        Assert.Equal(ResponsePublicationStatus.Published, response.Status);
        Assert.Equal(42, response.Payload);
        Assert.Equal(
            KernelError.ResponseNotPending,
            scenario.Kernel.RequestSessionCancellation(
                scenario.Caller,
                received.Value.Invocation).Error);
    }

    [Fact]
    public async Task AcceptedInFlightCancellationBlocksPublicationUntilServiceSettlesCancelled()
    {
        var scenario = CreateSession();
        using var cancellation = new CancellationTokenSource();
        var transport = new RuntimeSipClientTransport(
            scenario.Kernel,
            scenario.Caller,
            scenario.Session,
            cancellation.Token);
        var pending = transport.InvokeAsync(1).AsTask();
        var received = scenario.Kernel.ReceiveSessionRequest(scenario.Service, scenario.Session);
        Assert.True(received.IsSuccess, received.Message);
        Assert.True(scenario.Kernel.AcceptSessionInvocation(
            scenario.Service,
            received.Value!.Invocation,
            allowInFlightCancellation: true).IsSuccess);

        cancellation.Cancel();
        var requested = scenario.Kernel.QuerySessionCancellation(
            scenario.Service,
            received.Value.Invocation);
        Assert.True(requested.IsSuccess, requested.Message);
        Assert.True(requested.Value);
        Assert.True(scenario.Kernel.AcceptSessionCancellation(
            scenario.Service,
            received.Value.Invocation).IsSuccess);

        var forbiddenPublication = scenario.Kernel.PublishSessionResponse(
            scenario.Service,
            received.Value.Invocation,
            42);
        Assert.Equal(KernelError.InvalidTransition, forbiddenPublication.Error);
        Assert.True(scenario.Kernel.CancelSessionResponse(
            scenario.Service,
            received.Value.Invocation).IsSuccess);

        var response = await pending.WaitAsync(CompletionTimeout);
        Assert.Equal(ResponsePublicationStatus.Cancelled, response.Status);
    }

    [Fact]
    public void InvocationIdentityIsOwnerAndGenerationBound()
    {
        var scenario = CreateSession();
        var transport = new RuntimeSipClientTransport(
            scenario.Kernel,
            scenario.Caller,
            scenario.Session);
        _ = transport.InvokeAsync(1).AsTask();
        var received = scenario.Kernel.ReceiveSessionRequest(scenario.Service, scenario.Session);
        Assert.True(received.IsSuccess, received.Message);
        var foreign = TestFixtures.Create(scenario.Kernel, 703, 7030).Handle;

        Assert.Equal(
            KernelError.WrongSessionOwner,
            scenario.Kernel.RequestSessionCancellation(
                foreign,
                received.Value!.Invocation).Error);

        var stale = received.Value.Invocation with
        {
            Generation = new EndpointSessionInvocationGeneration(
                received.Value.Invocation.Generation.Value + 1)
        };
        Assert.Equal(
            KernelError.StaleGeneration,
            scenario.Kernel.RequestSessionCancellation(
                scenario.Caller,
                stale).Error);

        Assert.True(scenario.Kernel.CancelSessionResponse(
            scenario.Service,
            received.Value.Invocation).IsSuccess);
    }

    [Fact]
    public void CancellableLegacyRawChannelTransportFailsBeforeProtocolMutation()
    {
        var kernel = new RuntimeKernel();
        var caller = TestFixtures.Create(kernel, 711, 7110).Handle;
        var service = TestFixtures.Create(kernel, 712, 7120).Handle;
        var protocol = Protocol();
        var responses = Responses(protocol.ContractName);
        var channel = kernel.CreateChannel(caller, service, protocol, responses, 4).Value;
        using var cancellation = new CancellationTokenSource();
        var transport = new RuntimeSipClientTransport(
            kernel,
            caller,
            service,
            channel.Left,
            cancellationToken: cancellation.Token);

        var failure = Assert.Throws<InvalidOperationException>(
            () => transport.InvokeAsync(1).AsTask().GetAwaiter().GetResult());

        Assert.Contains("EndpointSessionHandle", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0UL, kernel.Channels.GetEndpoint(channel.Left).Value!.Sequence);
        Assert.Equal("Idle", kernel.Channels.GetEndpoint(channel.Left).Value!.ProtocolState);
    }

    [Fact]
    public async Task ConcurrentServiceReceiveCannotLoseExactInvocationCorrelation()
    {
        var scenario = CreateSession();
        var transport = new RuntimeSipClientTransport(
            scenario.Kernel,
            scenario.Caller,
            scenario.Session);

        for (var iteration = 0; iteration < 32; iteration++)
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var receiveTask = ReceiveEventuallyAsync(scenario, start.Task);
            var invokeTask = Task.Run(async () =>
            {
                await start.Task;
                return await transport.InvokeAsync(1);
            });
            start.SetResult();

            var received = await receiveTask.WaitAsync(CompletionTimeout);
            Assert.True(received.IsSuccess, received.Message);
            Assert.True(scenario.Kernel.AcceptSessionInvocation(
                scenario.Service,
                received.Value!.Invocation,
                allowInFlightCancellation: false).IsSuccess);
            Assert.True(scenario.Kernel.PublishSessionResponse(
                scenario.Service,
                received.Value.Invocation,
                iteration).IsSuccess);

            var response = await invokeTask.WaitAsync(CompletionTimeout);
            Assert.Equal(ResponsePublicationStatus.Published, response.Status);
            Assert.Equal(iteration, response.Payload);
        }
    }

    private static async Task<KernelResult<EndpointSessionRequestEnvelope>> ReceiveEventuallyAsync(
        SessionScenario scenario,
        Task start)
    {
        await start;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var received = scenario.Kernel.ReceiveSessionRequest(
                scenario.Service,
                scenario.Session);
            if (received.IsSuccess || received.Error != KernelError.InvalidMessage)
                return received;
            await Task.Delay(1);
        }

        return KernelResult<EndpointSessionRequestEnvelope>.Fail(
            KernelError.InvalidMessage,
            "Concurrent session receive did not observe the request.");
    }

    private static SessionScenario CreateSession()
    {
        var kernel = new RuntimeKernel();
        var caller = TestFixtures.Create(kernel, 701, 7010).Handle;
        var service = TestFixtures.Create(kernel, 702, 7020).Handle;
        var protocol = Protocol();
        var contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        var descriptor = kernel.RegisterService(
            service,
            "cancel-session",
            contract,
            protocol,
            Responses(protocol.ContractName)).Value!;
        var session = kernel.OpenSession(caller, descriptor, capacity: 4).Value;
        return new SessionScenario(kernel, caller, service, session);
    }

    private static ProtocolDefinitionV1 Protocol() =>
        new(
            "SessionCancellation",
            "session-cancellation-digest",
            "Idle",
            terminalStates: null,
            messages: [new ProtocolMessageDescriptorV1(1, "Run")],
            transitions: [new ProtocolTransitionV1(1, "Idle", "Idle")]);

    private static ResponseProtocolDefinitionV1 Responses(string contractName) =>
        new(
            contractName,
            "session-cancellation-response-digest",
            [new ResponseMessageDescriptorV1(
                1,
                "Run",
                new ResponsePayloadDescriptorV1(
                    ResponsePayloadKind.Primitive,
                    typeof(int).FullName!))]);

    private sealed record SessionScenario(
        RuntimeKernel Kernel,
        ProcessHandle Caller,
        ProcessHandle Service,
        EndpointSessionHandle Session);
}
