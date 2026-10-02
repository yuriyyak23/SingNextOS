using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class EndpointSessionCancellationTests
{
    [Fact]
    public async Task SettlementAdmissionWaitsForCorrelationGateAndCallbackReleasesIt()
    {
        var correlationGate = new object();
        var registry = new EndpointSessionInvocationRegistry(new CancellationScopeAuthority(TimeProvider.System), correlationGate);
        var caller = new ProcessHandle(new ProcessId(810), 1);
        var service = new ProcessHandle(new ProcessId(811), 1);
        var session = new EndpointSessionHandle(new EndpointSessionId(12), new EndpointSessionGeneration(1));
        var invocation = registry.Register(session, caller, service, 1, 1);
        Assert.True(registry.MarkDelivered(session, service, 1).IsSuccess);
        using var attempted = new ManualResetEventSlim();
        Task<KernelResult<ResponseEnvelope>> publication;
        lock (correlationGate)
        {
            publication = Task.Run(() =>
            {
                attempted.Set();
                return registry.Publish(invocation, service, () =>
                {
                    Assert.True(Task.Run(() =>
                    {
                        lock (correlationGate) return true;
                    }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
                    return KernelResult<ResponseEnvelope>.Ok(new(1, 1, ResponsePublicationStatus.Published, null));
                });
            });
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(publication.Wait(TimeSpan.FromMilliseconds(100)));
            Assert.Null(registry.SnapshotConsequence(invocation)!.Value.SettlementInProgress);
        }
        Assert.True((await publication.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DonationReturnReservationExcludesSettlementAndPreservesFaultConsequence(int fault)
    {
        var registry = new EndpointSessionInvocationRegistry(new CancellationScopeAuthority(TimeProvider.System));
        var caller = new ProcessHandle(new ProcessId(810), 1);
        var service = new ProcessHandle(new ProcessId(811), 1);
        var session = new EndpointSessionHandle(new EndpointSessionId(12), new EndpointSessionGeneration(1));
        var invocation = registry.Register(session, caller, service, 1, 1);
        Assert.True(registry.MarkDelivered(session, service, 1).IsSuccess);
        var donation = new ResourceDonationBinding(invocation, caller, service, default, default,
            caller, default, default, ResourceAssuranceV1.RuntimeEnforced, AdmissionQosHint.None, "owner-test");
        Assert.True(registry.BindResourceDonation(donation).IsSuccess);
        KernelResult CancelBudget(ResourceDonationBinding current)
        {
            Assert.Equal(donation, current);
            Assert.True(Task.Run(() => registry.SnapshotConsequence(invocation)!.Value.UnclosedPossibleEffect)
                .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            var callbacks = 0;
            Assert.Equal(KernelError.ResponseNotPending, registry.Publish(invocation, service, () =>
            {
                callbacks++;
                return KernelResult<ResponseEnvelope>.Ok(new(1, 1, ResponsePublicationStatus.Published, null));
            }).Error);
            Assert.Equal(0, callbacks);
            Assert.Equal(KernelError.InvalidTransition, registry.ActivateResourceDonation(invocation, service).Error);
            Assert.Equal(KernelError.InvalidTransition,
                registry.ReturnResourceDonationPreSubmit(invocation, service, _ => KernelResult.Ok()).Error);
            if (fault == 1) return KernelResult.Fail(KernelError.InvalidTransition, "budget denial");
            if (fault == 2) throw new InvalidOperationException("budget callback loss");
            if (fault == 3) registry.CloseSession(session);
            return KernelResult.Ok();
        }
        if (fault == 2)
            Assert.Throws<InvalidOperationException>(() => registry.ReturnResourceDonationPreSubmit(invocation, service, CancelBudget));
        else
            Assert.Equal(fault == 0 ? KernelError.None : KernelError.InvalidTransition,
                registry.ReturnResourceDonationPreSubmit(invocation, service, CancelBudget).Error);
        Assert.Equal(fault switch { 0 => ResourceDonationState.Returned, 1 => ResourceDonationState.Bound,
            _ => ResourceDonationState.Quarantined }, registry.SnapshotConsequence(invocation)!.Value.DonationState);
        if (fault == 1)
            Assert.True(registry.ReturnResourceDonationPreSubmit(invocation, service, _ => KernelResult.Ok()).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AmbiguousSettlementBlocksDonationBindingAndActivation(bool bindBeforeFailure)
    {
        var registry = new EndpointSessionInvocationRegistry(new CancellationScopeAuthority(TimeProvider.System));
        var caller = new ProcessHandle(new ProcessId(810), 1);
        var service = new ProcessHandle(new ProcessId(811), 1);
        var session = new EndpointSessionHandle(new EndpointSessionId(12), new EndpointSessionGeneration(1));
        var invocation = registry.Register(session, caller, service, 1, 1);
        Assert.True(registry.MarkDelivered(session, service, 1).IsSuccess);
        var donation = new ResourceDonationBinding(invocation, caller, service, default, default,
            caller, default, default, ResourceAssuranceV1.RuntimeEnforced, AdmissionQosHint.None, "owner-test");
        if (bindBeforeFailure) Assert.True(registry.BindResourceDonation(donation).IsSuccess);
        registry.SettlementReservedHook = () =>
        {
            var denied = bindBeforeFailure
                ? registry.ActivateResourceDonation(invocation, service)
                : registry.BindResourceDonation(donation);
            Assert.Equal(KernelError.InvalidTransition, denied.Error);
            if (bindBeforeFailure)
            {
                var budgetCalls = 0;
                Assert.Equal(KernelError.InvalidTransition,
                    registry.ReturnResourceDonationPreSubmit(invocation, service, _ =>
                    {
                        budgetCalls++;
                        return KernelResult.Ok();
                    }).Error);
                Assert.Equal(0, budgetCalls);
            }
        };
        Assert.Equal(KernelError.ServiceUnavailable, registry.Publish(invocation, service,
            () => KernelResult<ResponseEnvelope>.Fail(KernelError.ServiceUnavailable, "possible effect")).Error);
        var result = bindBeforeFailure
            ? registry.ActivateResourceDonation(invocation, service)
            : registry.BindResourceDonation(donation);
        Assert.Equal(KernelError.InvalidTransition, result.Error);
        if (bindBeforeFailure)
        {
            Assert.Equal(KernelError.InvalidTransition,
                registry.CloseResourceDonation(invocation, service, ResourceDonationState.Returned).Error);
        }
        Assert.Equal(bindBeforeFailure ? ResourceDonationState.Bound : (ResourceDonationState?)null,
            registry.SnapshotConsequence(invocation)!.Value.DonationState);
    }

    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ConcurrentReceiveAndSessionCloseCannotDeliverAfterClose()
    {
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var scenario = CreateSession();
            var transport = new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, scenario.Session);
            var pending = transport.InvokeAsync(1).AsTask();
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var receiving = Task.Run(async () =>
            {
                await start.Task;
                return scenario.Kernel.ReceiveSessionRequest(scenario.Service, scenario.Session);
            });
            var closing = Task.Run(async () =>
            {
                await start.Task;
                return scenario.Kernel.CloseSession(scenario.Caller, scenario.Session);
            });
            start.SetResult(true);
            await Task.WhenAll(receiving, closing).WaitAsync(CompletionTimeout);

            Assert.True((await closing).IsSuccess);
            var received = await receiving;
            Assert.True(received.IsSuccess || received.Error == KernelError.SessionClosed);
            Assert.Equal(KernelError.SessionClosed,
                scenario.Kernel.ReceiveSessionRequest(scenario.Service, scenario.Session).Error);
            Assert.Equal(ResponsePublicationStatus.Cancelled, (await pending.WaitAsync(CompletionTimeout)).Status);
            Assert.Equal(0, scenario.Kernel.Channels.InspectionSummary(scenario.Caller).Channels);
        }
    }

    [Fact]
    public async Task ClosedSessionRetainsAcceptedUnsettledInvocationAndBlocksProcessReclaim()
    {
        var scenario = CreateSession();
        var transport = new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, scenario.Session);
        var pending = transport.InvokeAsync(1).AsTask();
        var received = scenario.Kernel.ReceiveSessionRequest(scenario.Service, scenario.Session);
        Assert.True(received.IsSuccess, received.Message);
        Assert.True(scenario.Kernel.AcceptSessionInvocation(
            scenario.Service, received.Value!.Invocation, allowInFlightCancellation: false).IsSuccess);

        Assert.True(scenario.Kernel.CloseSession(scenario.Caller, scenario.Session).IsSuccess);
        Assert.Equal(ResponsePublicationStatus.Cancelled, (await pending.WaitAsync(CompletionTimeout)).Status);
        Assert.Equal(1, Assert.Single(scenario.Kernel.QueryProcessReclaimDiagnostics(scenario.Service)
            .Value!.Sessions).PendingInvocations);

        var teardown = scenario.Kernel.TerminateProcess(scenario.Service);
        Assert.Equal(KernelError.PlatformBindingDraining, teardown.Error);
        var snapshot = scenario.Kernel.QueryProcessTeardown(scenario.Service).Value!;
        Assert.False(snapshot.LocalReclaimCompleted);
        Assert.Equal(ProcessTeardownPhase.PlatformClosed, snapshot.Phase);
    }

    [Fact]
    public async Task ClosedSessionCanReclaimWhenRequestWasNeverAccepted()
    {
        var scenario = CreateSession();
        var transport = new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, scenario.Session);
        var pending = transport.InvokeAsync(1).AsTask();
        Assert.True(scenario.Kernel.ReceiveSessionRequest(scenario.Service, scenario.Session).IsSuccess);

        Assert.True(scenario.Kernel.CloseSession(scenario.Caller, scenario.Session).IsSuccess);
        Assert.Equal(ResponsePublicationStatus.Cancelled, (await pending.WaitAsync(CompletionTimeout)).Status);
        Assert.True(scenario.Kernel.TerminateProcess(scenario.Service).IsSuccess);
    }

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
        Assert.True(scenario.Kernel.CloseSession(scenario.Caller, scenario.Session).IsSuccess);
        Assert.True(scenario.Kernel.TerminateProcess(scenario.Service).IsSuccess);
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
    public async Task AcceptedCancelledResponseDoesNotProveEffectClosureForProcessReclaim()
    {
        var scenario = CreateSession();
        var transport = new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, scenario.Session);
        var pending = transport.InvokeAsync(1).AsTask();
        var received = scenario.Kernel.ReceiveSessionRequest(scenario.Service, scenario.Session);
        Assert.True(received.IsSuccess, received.Message);
        var scope = scenario.Kernel.CreateCancellationScope(scenario.Caller).Value!.Scope;
        Assert.True(scenario.Kernel.BindSessionCancellationScope(
            scenario.Caller, received.Value!.Invocation, scope).IsSuccess);
        Assert.True(scenario.Kernel.AcceptSessionInvocation(
            scenario.Service, received.Value!.Invocation, allowInFlightCancellation: true).IsSuccess);

        Assert.True(scenario.Kernel.CancelSessionResponse(
            scenario.Service, received.Value.Invocation).IsSuccess);
        Assert.Equal(ResponsePublicationStatus.Cancelled,
            (await pending.WaitAsync(CompletionTimeout)).Status);
        Assert.Equal(CancellationDisposition.TooLateEffectMayExist,
            scenario.Kernel.RequestSessionCancellation(
                scenario.Caller, received.Value.Invocation, scope).Value!.Disposition);
        Assert.True(scenario.Kernel.CloseSession(scenario.Caller, scenario.Session).IsSuccess);
        Assert.Equal(1, Assert.Single(scenario.Kernel.QueryProcessReclaimDiagnostics(scenario.Service)
            .Value!.Sessions).PendingInvocations);
        Assert.Equal(KernelError.PlatformBindingDraining,
            scenario.Kernel.TerminateProcess(scenario.Service).Error);
    }

    [Fact]
    public void FailedAcceptedInlineSettlementDoesNotAssertEffectContainment()
    {
        var scenario = CreateSession();
        var channel = scenario.Kernel.EndpointSessions.Resolve(
            scenario.Session, scenario.Caller).Value!.Channel;
        var inline = scenario.Kernel.BeginInlineSessionInvocation(
            scenario.Caller, scenario.Service, scenario.Session, 1, null);
        Assert.True(inline.IsSuccess, inline.Message);
        var invocation = inline.Value!.Context.Invocation;
        var scope = scenario.Kernel.CreateCancellationScope(scenario.Caller).Value!.Scope;
        Assert.True(scenario.Kernel.BindSessionCancellationScope(
            scenario.Caller, invocation, scope).IsSuccess);

        Assert.True(scenario.Kernel.SettleInlineSessionInvocation(
            scenario.Service, inline.Value, succeeded: false).IsSuccess);
        Assert.Equal(CancellationDisposition.TooLateEffectMayExist,
            scenario.Kernel.RequestSessionCancellation(
                scenario.Caller, invocation, scope).Value!.Disposition);
        Assert.True(scenario.Kernel.CloseSession(scenario.Caller, scenario.Session).IsSuccess);
        var consequence = scenario.Kernel.SnapshotSessionInvocationConsequence(invocation)!.Value;
        Assert.True(consequence.Delivered);
        Assert.True(consequence.ServiceAccepted);
        Assert.Equal(ResponsePublicationStatus.Cancelled, consequence.TerminalStatus);
        Assert.True(consequence.UnclosedPossibleEffect);
        Assert.False(consequence.AuthorizesClosure);
        Assert.False(consequence.AuthorizesReclaim);
        Assert.Null(scenario.Kernel.SnapshotSessionInvocationConsequence(invocation with
        { Generation = new EndpointSessionInvocationGeneration(invocation.Generation.Value + 1) }));
        Assert.Equal(EndpointSessionState.Closed,
            scenario.Kernel.EndpointSessions.SnapshotLifecycleTrace(scenario.Session)[^1].State);
        Assert.True(scenario.Kernel.Channels.SnapshotClosure(channel)!.Value.Closed);
        Assert.Equal(1, Assert.Single(scenario.Kernel.QueryProcessReclaimDiagnostics(
            scenario.Service).Value!.Sessions).PendingInvocations);
        Assert.Equal(KernelError.PlatformBindingDraining,
            scenario.Kernel.TerminateProcess(scenario.Service).Error);
    }

    [Fact]
    public void ConflictingInvocationScopeBindDoesNotConsumeRejectedScope()
    {
        var scopes = new CancellationScopeAuthority(TimeProvider.System);
        var registry = new EndpointSessionInvocationRegistry(scopes);
        var caller = new ProcessHandle(new ProcessId(810), 1);
        var service = new ProcessHandle(new ProcessId(811), 1);
        var session = new EndpointSessionHandle(new EndpointSessionId(12), new EndpointSessionGeneration(1));
        var first = registry.Register(session, caller, service, 1, 1);
        var second = registry.Register(session, caller, service, 2, 1);
        var boundScope = scopes.Create(caller, null, null).Value!.Scope;
        var rejectedScope = scopes.Create(caller, null, null).Value!.Scope;
        Assert.True(registry.BindCancellationScope(first, caller, boundScope).IsSuccess);
        var before = scopes.Observe(caller, rejectedScope).Value!.Sequence;

        Assert.Equal(KernelError.StaleGeneration,
            registry.BindCancellationScope(first, caller, rejectedScope).Error);
        Assert.Equal(before, scopes.Observe(caller, rejectedScope).Value!.Sequence);
        Assert.True(registry.BindCancellationScope(second, caller, rejectedScope).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration,
            registry.BindCancellationScope(first, caller, rejectedScope with
            { Generation = new CancellationScopeGeneration(rejectedScope.Generation.Value + 1) }).Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedQueuedSettlementCallbackRetainsPossibleEffectWithoutServiceAcceptance(bool throws)
    {
        var scopes = new CancellationScopeAuthority(TimeProvider.System);
        var registry = new EndpointSessionInvocationRegistry(scopes);
        var caller = new ProcessHandle(new ProcessId(810), 1);
        var service = new ProcessHandle(new ProcessId(811), 1);
        var session = new EndpointSessionHandle(new EndpointSessionId(12), new EndpointSessionGeneration(1));
        var invocation = registry.Register(session, caller, service, 1, 1);
        Assert.True(registry.MarkDelivered(session, service, 1).IsSuccess);
        var scope = scopes.Create(caller, null, null).Value!.Scope;
        Assert.True(registry.BindCancellationScope(invocation, caller, scope).IsSuccess);
        var callbackEntered = false;
        registry.SettlementReservedHook = () =>
        {
            Assert.Equal(KernelError.InvalidTransition,
                registry.AcceptInvocation(invocation, service, allowInFlightCancellation: false).Error);
            Assert.False(registry.SnapshotConsequence(invocation)!.Value.ServiceAccepted);
        };

        if (throws)
        {
            Assert.Throws<InvalidOperationException>(() => registry.Publish(invocation, service, () =>
            {
                callbackEntered = true;
                throw new InvalidOperationException("Injected failure after possible effect.");
            }));
        }
        else
        {
            var failed = registry.Publish(invocation, service, () =>
            {
                callbackEntered = true;
                return KernelResult<ResponseEnvelope>.Fail(
                    KernelError.ServiceUnavailable, "Injected ambiguous settlement failure.");
            });
            Assert.Equal(KernelError.ServiceUnavailable, failed.Error);
        }

        Assert.True(callbackEntered);
        var failedSnapshot = registry.SnapshotConsequence(invocation)!.Value;
        Assert.False(failedSnapshot.ServiceAccepted);
        Assert.Null(failedSnapshot.SettlementInProgress);
        Assert.Null(failedSnapshot.TerminalStatus);
        Assert.True(failedSnapshot.UnclosedPossibleEffect);
        Assert.Equal(KernelError.InvalidTransition,
            registry.AcceptInvocation(invocation, service, allowInFlightCancellation: false).Error);
        Assert.False(registry.SnapshotConsequence(invocation)!.Value.ServiceAccepted);
        Assert.Equal(CancellationDisposition.TooLateEffectMayExist,
            registry.RequestCancellation(invocation, caller, scope).Value!.Disposition);

        // A later successful response or cancellation does not close the earlier ambiguity.
        var terminal = throws ? ResponsePublicationStatus.Cancelled : ResponsePublicationStatus.Published;
        var settled = throws
            ? registry.Cancel(invocation, service, () => KernelResult<ResponseEnvelope>.Ok(
                new ResponseEnvelope(1, 1, terminal, null)))
            : registry.Publish(invocation, service, () => KernelResult<ResponseEnvelope>.Ok(
                new ResponseEnvelope(1, 1, terminal, null)));
        Assert.True(settled.IsSuccess);
        Assert.Equal(terminal, registry.SnapshotConsequence(invocation)!.Value.TerminalStatus);
        Assert.Equal(CancellationDisposition.TooLateEffectMayExist,
            registry.RequestCancellation(invocation, caller, scope).Value!.Disposition);
        registry.CloseSession(session);
        Assert.True(registry.SnapshotConsequence(invocation)!.Value.UnclosedPossibleEffect);
        Assert.Equal(1, registry.UnclosedPossibleEffectCount(session));
    }

    [Fact]
    public async Task AcceptedCancellationAndSessionCloseRetainPossibleEffectInEitherOrder()
    {
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var scenario = CreateSession();
            var transport = new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, scenario.Session);
            var pending = transport.InvokeAsync(1).AsTask();
            var received = scenario.Kernel.ReceiveSessionRequest(scenario.Service, scenario.Session);
            Assert.True(received.IsSuccess, received.Message);
            Assert.True(scenario.Kernel.AcceptSessionInvocation(
                scenario.Service, received.Value!.Invocation, allowInFlightCancellation: true).IsSuccess);
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelling = Task.Run(async () =>
            {
                await start.Task;
                return scenario.Kernel.CancelSessionResponse(scenario.Service, received.Value.Invocation);
            });
            var closing = Task.Run(async () =>
            {
                await start.Task;
                return scenario.Kernel.CloseSession(scenario.Caller, scenario.Session);
            });
            start.SetResult(true);
            await Task.WhenAll(cancelling, closing).WaitAsync(CompletionTimeout);

            Assert.True((await closing).IsSuccess);
            var cancellation = await cancelling;
            Assert.True(cancellation.IsSuccess || cancellation.Error is KernelError.SessionClosed or KernelError.StaleGeneration,
                $"Cancellation outcome: {cancellation.Error}: {cancellation.Message}");
            Assert.Equal(ResponsePublicationStatus.Cancelled,
                (await pending.WaitAsync(CompletionTimeout)).Status);
            Assert.True(scenario.Kernel.SnapshotSessionInvocationConsequence(
                received.Value.Invocation)!.Value.UnclosedPossibleEffect);
            Assert.Equal(1, Assert.Single(scenario.Kernel.QueryProcessReclaimDiagnostics(scenario.Service)
                .Value!.Sessions).PendingInvocations);
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.TerminateProcess(scenario.Service).Error);
        }
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

    [Fact]
    public async Task PublicationCallbackRunsOutsideInvocationOwnerLockAndSettlementHasOneWinner()
    {
        var scenario = CreateSession();
        var transport = new RuntimeSipClientTransport(
            scenario.Kernel,
            scenario.Caller,
            scenario.Session);
        var pending = transport.InvokeAsync(1).AsTask();
        var received = scenario.Kernel.ReceiveSessionRequest(scenario.Service, scenario.Session);
        Assert.True(received.IsSuccess, received.Message);
        Assert.True(scenario.Kernel.AcceptSessionInvocation(
            scenario.Service,
            received.Value!.Invocation,
            allowInFlightCancellation: false).IsSuccess);

        var reserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Kernel.SessionInvocationSettlementReservedHook = () =>
        {
            reserved.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };

        var first = Task.Run(() => scenario.Kernel.PublishSessionResponse(
            scenario.Service,
            received.Value.Invocation,
            42));
        await reserved.Task.WaitAsync(CompletionTimeout);

        var cancellationCheck = Task.Run(() => scenario.Kernel.RequestSessionCancellation(
            scenario.Caller,
            received.Value.Invocation));
        var cancellationResult = await cancellationCheck.WaitAsync(CompletionTimeout);
        Assert.Equal(KernelError.InvalidTransition, cancellationResult.Error);

        var competing = scenario.Kernel.PublishSessionResponse(
            scenario.Service,
            received.Value.Invocation,
            43);
        Assert.Equal(KernelError.ResponseNotPending, competing.Error);

        release.TrySetResult();
        var published = await first.WaitAsync(CompletionTimeout);
        Assert.True(published.IsSuccess, published.Message);
        var response = await pending.WaitAsync(CompletionTimeout);
        Assert.Equal(42, response.Payload);
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
