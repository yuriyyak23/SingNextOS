using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class ServiceDiscoverySessionTests
{
    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)]
    public async Task ScopedInvocationWrapperValidatesSessionAfterDeadlineClock(bool request, int fault)
    {
        SessionScopeClock clock = new SessionScopeClock();
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(clock, withResponses: true);
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        Task<KernelResult<ResponseEnvelope>> pending = kernel.InvokeSessionAsync(caller, session, 1, null).AsTask();
        EndpointSessionRequestEnvelope received = kernel.ReceiveSessionRequest(provider, session).Value!;
        CancellationObservation scope = kernel.CreateCancellationScope(caller, new(200)).Value!;
        if (request) Assert.True(kernel.BindSessionCancellationScope(caller, received.Invocation, scope.Scope).IsSuccess);
        CancellationObservation before = kernel.ObserveCancellation(caller, scope.Scope).Value!;
        clock.Callback = () =>
        {
            if (fault == 2) throw new InvalidOperationException("scope clock fault");
            if (fault == 0)
            {
                Task<KernelResult> worker = Task.Run(() => kernel.RevokeCapability(capability));
                Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
                Assert.True(worker.Result.IsSuccess);
            }
        };
        KernelError Invoke() => request
            ? kernel.RequestSessionCancellation(caller, received.Invocation, scope.Scope).Error
            : kernel.BindSessionCancellationScope(caller, received.Invocation, scope.Scope).Error;
        if (fault == 2) Assert.Throws<InvalidOperationException>(() => Invoke());
        else Assert.Equal(fault == 0 ? KernelError.CapabilityRevoked : KernelError.None, Invoke());
        CancellationObservation observed = kernel.ObserveCancellation(caller, scope.Scope).Value!;
        if (fault != 1)
        {
            Assert.Equal(before.Sequence, observed.Sequence);
            Assert.False(kernel.QuerySessionCancellation(provider, received.Invocation).Value);
        }
        else Assert.Equal(request, observed.CancellationRequested);
        Assert.False(observed.AuthorizesEffect);
        Assert.False(observed.AuthorizesReclaim);
        Assert.True(kernel.CancelSessionResponse(provider, received.Invocation).IsSuccess);
        Assert.Equal(ResponsePublicationStatus.Cancelled, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Value!.Status);
    }

    private sealed class SessionScopeClock : TimeProvider
    {
        public Action? Callback;
        public override long GetTimestamp()
        {
            Interlocked.Exchange(ref Callback, null)?.Invoke();
            return 100;
        }
    }

    [Fact]
    public void InlineCopiedPermissionRevokeDuringMetadataCapturePreservesChannelFact()
    {
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(bounded: true);
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        ChannelEndpointHandle channel = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
        ChannelEndpoint before = kernel.Channels.GetEndpoint(channel).Value!;
        CallbackPayload payload = new CallbackPayload(() =>
        {
            Task<KernelResult> revoke = Task.Run(() => kernel.RevokeCapability(capability));
            Assert.True(revoke.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(revoke.Result.IsSuccess);
        });
        KernelResult<InlineSipInvocationLease> result = kernel.BeginInlineSessionInvocation(caller, provider, session, 1, payload);
        Assert.Equal(KernelError.MissingCapability, result.Error);
        Assert.Equal(before, kernel.Channels.GetEndpoint(channel).Value);
        Assert.Equal(0, kernel.EndpointSessions.ActivePinCount(session));
        Assert.Null(kernel.SnapshotSessionInvocationConsequence(new(session, new(1), new(1))));
        Assert.True(kernel.CloseSession(caller, session).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void InlineCopiedMetadataProcessTerminationCannotPublishAcceptedInvocation(bool terminateService)
    {
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(bounded: true);
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        CallbackPayload payload = new CallbackPayload(() =>
        {
            Task<KernelResult> termination = Task.Run(() => kernel.TerminateProcess(terminateService ? provider : caller));
            Assert.True(termination.Wait(TimeSpan.FromSeconds(5)), "Inline metadata held lifecycle locks.");
        });
        Assert.False(kernel.BeginInlineSessionInvocation(caller, provider, session, 1, payload).IsSuccess);
        Assert.Equal(0, kernel.EndpointSessions.ActivePinCount(session));
        Assert.Null(kernel.SnapshotSessionInvocationConsequence(new(session, new(1), new(1))));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CopiedSessionPermissionIsRevalidatedAfterPayloadCallbackBeforeEnqueue(bool scoped)
    {
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(bounded: true, withResponses: true);
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        ChannelEndpointHandle channel = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
        CancellationScopeHandle scope = kernel.CreateCancellationScope(caller).Value!.Scope;
        ChannelEndpoint before = kernel.Channels.GetEndpoint(channel).Value!;
        CallbackPayload payload = new CallbackPayload(() =>
        {
            Task<KernelResult> revoke = Task.Run(() => kernel.RevokeCapability(capability));
            Assert.True(revoke.Wait(TimeSpan.FromSeconds(5)), "Caller metadata capture held capability locks.");
            Assert.True(revoke.Result.IsSuccess);
        });
        ValueTask<KernelResult<ResponseEnvelope>> pending = scoped
            ? kernel.InvokeSessionUntilCancellationAsync(caller, session, 1, payload, scope, CancellationToken.None)
            : kernel.InvokeSessionAsync(caller, session, 1, payload);
        KernelResult<ResponseEnvelope> result = await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(KernelError.MissingCapability, result.Error);
        Assert.Equal(before, kernel.Channels.GetEndpoint(channel).Value);
        Assert.Equal(0, kernel.Channels.InspectionSummary(caller).QueuedMessages);
        Assert.Equal(0, kernel.EndpointSessions.ActivePinCount(session));
        Assert.True(kernel.CloseSession(caller, session).IsSuccess);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OrdinaryQueuedCallersRecheckPinAfterPayloadCaptureBeforeAnySend(bool pair)
    {
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(bounded: true, withResponses: true);
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        ChannelEndpointHandle channel = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
        OwnedBuffer<byte> untouched = kernel.AllocateBuffer<byte>(caller, 8).Value!;
        RegionHandle original = untouched.Handle;
        CallbackPayload payload = new CallbackPayload(() =>
        {
            Task<KernelResult> close = Task.Run(() => kernel.CloseSession(caller, session));
            Assert.True(close.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.SessionDraining, close.Result.Error);
        });
        KernelResult<ResponseEnvelope> result = pair
            ? await kernel.InvokeSessionOwnershipPairAsync(caller, session, 1, payload, untouched)
            : await kernel.InvokeSessionAsync(caller, session, 1, payload);
        Assert.Equal(KernelError.SessionClosed, result.Error);
        Assert.Equal(0, kernel.EndpointSessions.ActivePinCount(session));
        Assert.True(kernel.Channels.SnapshotClosure(channel)!.Value.Closed);
        Assert.Equal(0, kernel.Channels.InspectionSummary(caller).QueuedMessages);
        Assert.True(untouched.IsValid);
        Assert.Equal(original, untouched.Handle);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task QueuedPinFinalizationClosesOriginalChannelAfterPayloadCloseAndThrow(int path)
    {
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(bounded: true, withResponses: true);
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        ChannelEndpointHandle channel = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
        CancellationScopeHandle scope = kernel.CreateCancellationScope(caller).Value!.Scope;
        CallbackPayload payload = new CallbackPayload(() =>
        {
            Assert.Equal(KernelError.SessionDraining, kernel.CloseSession(caller, session).Error);
            throw new InvalidOperationException("Queued caller preparation interrupted.");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (path == 0) await kernel.InvokeSessionAsync(caller, session, 1, payload);
            else if (path == 1) await kernel.InvokeSessionUntilCancellationAsync(caller, session, 1, payload, scope, CancellationToken.None);
            else await kernel.InvokeSessionOwnershipPairAsync(caller, session, 1, payload, new object());
        });
        Assert.Equal(0, kernel.EndpointSessions.ActivePinCount(session));
        Assert.True(kernel.Channels.SnapshotClosure(channel)!.Value.Closed);
        Assert.Equal(0, kernel.Channels.InspectionSummary(caller).QueuedMessages);
    }
    [Fact]
    public async Task ScopedInvocationRechecksPinAfterPayloadCallbackBeforeEnqueue()
    {
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(bounded: true, withResponses: true);
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        ChannelEndpointHandle channel = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
        CancellationScopeHandle scope = kernel.CreateCancellationScope(caller).Value!.Scope;
        CallbackPayload payload = new CallbackPayload(() =>
        {
            Task<KernelResult> close = Task.Run(() => kernel.CloseSession(caller, session));
            Assert.True(close.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.SessionDraining, close.Result.Error);
        });
        KernelResult<ResponseEnvelope> result = await kernel.InvokeSessionUntilCancellationAsync(caller, session, 1, payload,
            scope, CancellationToken.None);
        Assert.Equal(KernelError.SessionClosed, result.Error);
        Assert.Equal(0, kernel.Channels.InspectionSummary(caller).QueuedMessages);
        Assert.Equal(1, payload.SizeReads);
        Assert.Equal(0, kernel.EndpointSessions.ActivePinCount(session));
        Assert.True(kernel.Channels.SnapshotClosure(channel)!.Value.Closed);
    }
    [Fact]
    public async Task ScopedInvocationIsBoundBeforeTraceReentryCancellationAndServiceAcceptance()
    {
        IpcTraceClock clock = new IpcTraceClock();
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(clock, withResponses: true);
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        CancellationScopeHandle scope = kernel.CreateCancellationScope(caller).Value!.Scope;
        Assert.True(kernel.StartTraceSession(caller, 32).IsSuccess);
        clock.Callback = () =>
        {
            Task<bool> worker = Task.Run(() =>
            {
                KernelResult<EndpointSessionRequestEnvelope> received = kernel.ReceiveSessionRequest(provider, session);
                if (!received.IsSuccess) return false;
                KernelResult<CancellationObservation> cancellation = kernel.RequestSessionCancellation(caller, received.Value!.Invocation, scope);
                if (!cancellation.IsSuccess) return false;
                if (kernel.AcceptSessionInvocation(provider, received.Value.Invocation, false).IsSuccess) return false;
                return kernel.AcceptSessionCancellation(provider, received.Value.Invocation).IsSuccess &&
                    kernel.CancelSessionResponse(provider, received.Value.Invocation).IsSuccess;
            });
            clock.CallbackSucceeded = worker.Wait(TimeSpan.FromSeconds(5)) && worker.Result;
        };
        Task<KernelResult<ResponseEnvelope>> pending = kernel.InvokeSessionUntilCancellationAsync(caller, session, 1, null, scope,
            CancellationToken.None).AsTask();
        Assert.True(clock.CallbackSucceeded, "Exact scope was absent before trace reentry or service effect acceptance escaped cancellation.");
        KernelResult<ResponseEnvelope> result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(CancellationDisposition.CancelledBeforeEffect,
            kernel.ObserveCancellation(caller, scope).Value!.Disposition);
        Assert.True(kernel.CloseSession(caller, session).IsSuccess);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SessionReceiveTraceReentryOrThrowPreservesDeliveredCorrelation(bool throws)
    {
        IpcTraceClock clock = new IpcTraceClock();
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(clock, withResponses: true);
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        ChannelEndpointHandle channel = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
        Assert.True(kernel.StartTraceSession(provider, 32).IsSuccess);
        KernelResult<ChannelEnvelope> sent = kernel.Send(caller, provider, channel, 1);
        Assert.True(sent.IsSuccess, sent.Message);
        clock.Callback = () =>
        {
            Task<KernelResult> worker = Task.Run(() => kernel.CloseSession(caller, session));
            clock.CallbackSucceeded = worker.Wait(TimeSpan.FromSeconds(5)) && worker.Result.IsSuccess;
            if (throws) throw new InvalidOperationException("Session receive trace interrupted.");
        };
        KernelResult<EndpointSessionRequestEnvelope> received = kernel.ReceiveSessionRequest(provider, session);
        Assert.True(received.IsSuccess, received.Message);
        Assert.True(clock.CallbackSucceeded, "Session receive retained outer commit locks during observation.");
        Assert.Equal(sent.Value!.Sequence, received.Value!.Invocation.InvocationId.Value);
        Assert.Equal(0, kernel.Channels.InspectionSummary(caller).QueuedMessages);
        Assert.True(kernel.Channels.SnapshotClosure(channel)!.Value.Closed);
    }
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void SendBudgetObservationReentryOrThrowPreservesCommitOrRefund(bool reject, bool throws)
    {
        IpcTraceClock clock = new IpcTraceClock();
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(clock, bounded: true);
        CapabilityId budgetCapability = kernel.MintCapability(new DomainId(20), provider, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle account = kernel.AdmitProcessBudget(provider, budgetCapability, caller, "send-observation",
            [new(ServiceBudgetDimension.IpcMessages, 1), new(ServiceBudgetDimension.IpcBytes, 36)]).Value!.ProcessBudget;
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        ChannelEndpointHandle channel = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
        ChannelEndpointHandle peer = new ChannelEndpointHandle(channel.ChannelId, new EndpointId(2), channel.Generation);
        Assert.True(kernel.StartTraceSession(caller, 32).IsSuccess);
        IReadOnlyList<BudgetUsage> before = kernel.QueryBudget(account).Value!.Usage;
        ChannelEnvelope? observed = null;
        clock.Callback = () =>
        {
            Task<bool> worker = Task.Run(() =>
            {
                if (reject) return kernel.CloseSession(caller, session).IsSuccess;
                KernelResult<ChannelEnvelope> received = kernel.Receive(provider, peer);
                observed = received.Value;
                return received.IsSuccess;
            });
            clock.CallbackSucceeded = worker.Wait(TimeSpan.FromSeconds(5)) && worker.Result;
            if (throws) throw new InvalidOperationException("Send budget observation interrupted.");
        };
        KernelResult<ChannelEnvelope> sent = kernel.Send(caller, provider, channel, 1, new CallbackPayload(() => { }, reject ? -1 : 4));
        Assert.True(clock.CallbackSucceeded, "Budget observation held send locks or ran before commit/refund.");
        if (reject) Assert.Equal(KernelError.UnsupportedPayload, sent.Error);
        else { Assert.True(sent.IsSuccess, sent.Message); Assert.Same(sent.Value, observed); }
        Assert.Equal(before, kernel.QueryBudget(account).Value!.Usage);
        Assert.Equal(0, kernel.Channels.InspectionSummary(caller).QueuedMessages);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ReceiveBudgetTraceWorkerCloseOrThrowPreservesDeliveryAndReleasedUsage(bool throws)
    {
        IpcTraceClock clock = new IpcTraceClock();
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(clock);
        CapabilityId budgetCapability = kernel.MintCapability(new DomainId(20), provider, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle account = kernel.AdmitProcessBudget(provider, budgetCapability, caller, "release-trace",
            [new(ServiceBudgetDimension.IpcMessages, 1), new(ServiceBudgetDimension.IpcBytes, 32)]).Value!.ProcessBudget;
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        ChannelEndpointHandle channel = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
        ChannelEndpointHandle peer = new ChannelEndpointHandle(channel.ChannelId, new EndpointId(2), channel.Generation);
        Assert.True(kernel.StartTraceSession(caller, 32).IsSuccess);
        IReadOnlyList<BudgetUsage> before = kernel.QueryBudget(account).Value!.Usage;
        KernelResult<ChannelEnvelope> sent = kernel.Send(caller, provider, channel, 1);
        Assert.True(sent.IsSuccess, sent.Message);
        clock.Callback = () =>
        {
            Task<KernelResult> worker = Task.Run(() => kernel.CloseSession(caller, session));
            clock.CallbackSucceeded = worker.Wait(TimeSpan.FromSeconds(5)) && worker.Result.IsSuccess;
            if (throws) throw new InvalidOperationException("Budget release trace interrupted.");
        };
        KernelResult<ChannelEnvelope> delivered = kernel.Receive(provider, peer);
        Assert.True(delivered.IsSuccess, delivered.Message);
        Assert.True(clock.CallbackSucceeded, "Budget release trace held receive commit locks.");
        Assert.Same(sent.Value, delivered.Value);
        Assert.Equal(before, kernel.QueryBudget(account).Value!.Usage);
        Assert.Equal(0, kernel.Channels.InspectionSummary(caller).QueuedMessages);
        Assert.True(kernel.Channels.SnapshotClosure(channel)!.Value.Closed);
    }
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void IpcTraceClockReentryOrThrowPreservesCommittedEnvelope(bool receive, bool throws)
    {
        IpcTraceClock clock = new IpcTraceClock();
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(clock);
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        ChannelEndpointHandle channel = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
        ChannelEndpointHandle peer = new ChannelEndpointHandle(channel.ChannelId, new EndpointId(2), channel.Generation);
        Assert.True(kernel.StartTraceSession(receive ? provider : caller, 16).IsSuccess);
        ChannelEnvelope? observed = null;
        clock.Callback = () =>
        {
            Task<bool> worker = Task.Run(() =>
            {
                if (receive) return kernel.CloseSession(caller, session).IsSuccess;
                KernelResult<ChannelEnvelope> delivered = kernel.Receive(provider, peer);
                observed = delivered.Value;
                return delivered.IsSuccess;
            });
            if (!worker.Wait(TimeSpan.FromSeconds(5)))
            {
                clock.CallbackSucceeded = false;
                return;
            }
            clock.CallbackSucceeded = worker.Result;
            if (throws) throw new InvalidOperationException("Trace clock interrupted.");
        };
        if (receive) Assert.True(kernel.Send(caller, provider, channel, 1).IsSuccess);
        KernelResult<ChannelEnvelope> result = receive ? kernel.Receive(provider, peer) : kernel.Send(caller, provider, channel, 1);
        Assert.True(result.IsSuccess, result.Message);
        Assert.True(clock.CallbackSucceeded, "Trace clock held IPC commit locks or lost the committed effect.");
        Assert.Equal(1UL, result.Value!.Sequence);
        if (!receive) Assert.Same(result.Value, observed);
        Assert.Equal(0, kernel.Channels.InspectionSummary(caller).QueuedMessages);
    }

    private sealed class IpcTraceClock : TimeProvider
    {
        internal Action? Callback;
        internal bool CallbackSucceeded;
        public override long GetTimestamp()
        {
            Interlocked.Exchange(ref Callback, null)?.Invoke();
            return 1;
        }
    }
    [Fact]
    public void QueuedCapabilityCaptureCallbackRunsBeforeReservationAndChannelCommit()
    {
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture();
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        ChannelEndpointHandle channel = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
        OpeningCapabilities capabilities = new OpeningCapabilities(capability, () =>
        {
            Task<KernelResult> close = Task.Run(() => kernel.CloseSession(caller, session));
            Assert.True(close.Wait(TimeSpan.FromSeconds(5)), "Capability capture held queued commit locks.");
            Assert.True(close.Result.IsSuccess);
        });
        Assert.False(kernel.Send(caller, provider, channel, 1, capabilities: capabilities).IsSuccess);
        Assert.Equal(0, kernel.Channels.InspectionSummary(caller).QueuedMessages);
        Assert.True(kernel.Channels.SnapshotClosure(channel)!.Value.Closed);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void QueuedPayloadCallbackCloseOrThrowPreventsQueuePublication(bool throws)
    {
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(bounded: true);
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        ChannelEndpointHandle channel = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
        CallbackPayload payload = new CallbackPayload(() =>
        {
            Task<KernelResult> close = Task.Run(() => kernel.CloseSession(caller, session));
            Assert.True(close.Wait(TimeSpan.FromSeconds(5)), "Payload getter held queued commit locks.");
            Assert.True(close.Result.IsSuccess);
            if (throws) throw new InvalidOperationException("Queued payload interrupted.");
        });
        if (throws)
            Assert.Throws<InvalidOperationException>(() => kernel.Send(caller, provider, channel, 1, payload));
        else
            Assert.False(kernel.Send(caller, provider, channel, 1, payload).IsSuccess);
        Assert.Equal(0, kernel.Channels.InspectionSummary(caller).QueuedMessages);
        Assert.True(kernel.Channels.SnapshotClosure(channel)!.Value.Closed);
        Assert.Equal(1, payload.SizeReads);
    }

    [Fact]
    public void QueuedPayloadUsesSameSingleSizeObservationForBudgetAndValidation()
    {
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(bounded: true);
        CapabilityId budgetCapability = kernel.MintCapability(new DomainId(20), provider, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle account = kernel.AdmitProcessBudget(provider, budgetCapability, caller, "copied-queue",
            [new(ServiceBudgetDimension.IpcMessages, 1), new(ServiceBudgetDimension.IpcBytes, 36)]).Value!.ProcessBudget;
        IReadOnlyList<BudgetUsage> before = kernel.QueryBudget(account).Value!.Usage;
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        ChannelEndpointHandle channel = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
        CallbackPayload payload = new CallbackPayload(() => { });
        KernelResult<ChannelEnvelope> sent = kernel.Send(caller, provider, channel, 1, payload);
        Assert.True(sent.IsSuccess, sent.Message);
        Assert.Equal(1, payload.SizeReads);
        Assert.Equal(1, payload.LimitReads);
        ChannelEndpointHandle peer = new ChannelEndpointHandle(channel.ChannelId, new EndpointId(2), channel.Generation);
        KernelResult<ChannelEnvelope> received = kernel.Receive(provider, peer);
        Assert.True(received.IsSuccess, received.Message);
        Assert.Same(payload, received.Value!.Payload);
        Assert.Equal(before, kernel.QueryBudget(account).Value!.Usage);
        Assert.True(kernel.CloseSession(caller, session).IsSuccess);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CopiedPayloadCallbackCloseOrThrowBalancesPinBeforePublication(bool throws)
    {
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(bounded: true);
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        EndpointSessionRegistry.Record record = kernel.EndpointSessions.Resolve(session, caller).Value!;
        CallbackPayload payload = new CallbackPayload(() =>
        {
            Task<KernelResult> close = Task.Run(() => kernel.CloseSession(caller, session));
            Assert.True(close.Wait(TimeSpan.FromSeconds(5)), "Payload getter held correlation locks.");
            Assert.Equal(KernelError.SessionDraining, close.Result.Error);
            if (throws) throw new InvalidOperationException("Payload interrupted.");
        });
        if (throws)
            Assert.Throws<InvalidOperationException>(() => kernel.BeginInlineSessionInvocation(caller, provider, session, 1, payload));
        else
            Assert.False(kernel.BeginInlineSessionInvocation(caller, provider, session, 1, payload).IsSuccess);
        Assert.Equal(EndpointSessionState.Closed, record.State);
        Assert.True(kernel.Channels.SnapshotClosure(record.Channel)!.Value.Closed);
        Assert.Equal(1, payload.SizeReads);
    }

    [Fact]
    public void CopiedPayloadMetadataIsReadOnceBeforeOwnerPublication()
    {
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(bounded: true);
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        CallbackPayload payload = new CallbackPayload(() => { });
        KernelResult<InlineSipInvocationLease> invocation = kernel.BeginInlineSessionInvocation(caller, provider, session, 1, payload);
        Assert.True(invocation.IsSuccess, invocation.Message);
        Assert.Equal(1, payload.SizeReads);
        Assert.Equal(1, payload.LimitReads);
        Assert.True(kernel.SettleInlineSessionInvocation(provider, invocation.Value!, succeeded: true).IsSuccess);
        Assert.True(kernel.CloseSession(caller, session).IsSuccess);
    }

    [Theory]
    [InlineData(-1, 16)] [InlineData(17, 16)] [InlineData(4, 15)]
    public void CopiedPayloadInvalidMetadataRefusesWithoutConsumingTransition(int size, int limit)
    {
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(bounded: true);
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        CallbackPayload invalid = new CallbackPayload(() => { }, size, limit);
        Assert.Equal(KernelError.UnsupportedPayload,
            kernel.BeginInlineSessionInvocation(caller, provider, session, 1, invalid).Error);
        Assert.Equal(1, invalid.SizeReads);
        Assert.Equal(1, invalid.LimitReads);
        KernelResult<InlineSipInvocationLease> valid = kernel.BeginInlineSessionInvocation(caller, provider, session, 1, new CallbackPayload(() => { }));
        Assert.True(valid.IsSuccess, valid.Message);
        Assert.True(kernel.SettleInlineSessionInvocation(provider, valid.Value!, succeeded: true).IsSuccess);
        Assert.True(kernel.CloseSession(caller, session).IsSuccess);
    }

    private sealed class CallbackPayload(Action callback, int size = 4, int limit = 16) : IBoundedPayload
    {
        public int SizeReads;
        public int LimitReads;
        public int PayloadSize { get { SizeReads++; callback(); return size; } }
        public int MaxPayloadSize { get { LimitReads++; return limit; } }
    }
    [Theory]
    [InlineData(0UL)] [InlineData(ulong.MaxValue)]
    public void SessionCapacityRefusalPreservesOriginalFactsAndNeverWraps(ulong nextId)
    {
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture();
        EndpointSessionHandle original = kernel.OpenSession(caller, descriptor, [capability]).Value;
        EndpointSessionRegistry.Record originalRecord = kernel.EndpointSessions.Resolve(original, caller).Value!;
        SetAllocator(kernel.EndpointSessions, "_nextId", nextId);
        if (nextId != 0)
        {
            KernelResult<EndpointSessionHandle> final = kernel.OpenSession(caller, descriptor, [capability]);
            Assert.True(final.IsSuccess, final.Message);
            Assert.Equal(ulong.MaxValue, final.Value.SessionId.Value);
            Assert.True(kernel.CloseSession(caller, final.Value).IsSuccess);
        }
        int sessionCount = kernel.EndpointSessions.SnapshotForProcess(caller).Length;
        int callerChannels = kernel.Processes.Resolve(caller).Value!.Channels.Count;
        int providerChannels = kernel.Processes.Resolve(provider).Value!.Channels.Count;
        for (int attempt = 0; attempt < 3; attempt++)
            Assert.Equal(KernelError.CapacityExhausted, kernel.OpenSession(caller, descriptor, [capability]).Error);
        Assert.Equal(sessionCount, kernel.EndpointSessions.SnapshotForProcess(caller).Length);
        Assert.Equal(callerChannels, kernel.Processes.Resolve(caller).Value!.Channels.Count);
        Assert.Equal(providerChannels, kernel.Processes.Resolve(provider).Value!.Channels.Count);
        Assert.Same(originalRecord, kernel.EndpointSessions.Resolve(original, caller).Value);
        Assert.Equal(EndpointSessionState.Active, originalRecord.State);
        Assert.Single(kernel.EndpointSessions.SnapshotLifecycleTrace(original));
        Assert.True(kernel.CloseSession(caller, original).IsSuccess);
    }

    [Fact]
    public async Task ConcurrentOpeningsUseFinalSessionIdentityExactlyOnce()
    {
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture();
        SetAllocator(kernel.EndpointSessions, "_nextId", ulong.MaxValue);
        KernelResult<EndpointSessionHandle>[] results = await Task.WhenAll(Task.Run(() => kernel.OpenSession(caller, descriptor, [capability])),
            Task.Run(() => kernel.OpenSession(caller, descriptor, [capability]))).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ulong.MaxValue, Assert.Single(results, result => result.IsSuccess).Value.SessionId.Value);
        Assert.Equal(KernelError.CapacityExhausted, Assert.Single(results, result => !result.IsSuccess).Error);
        Assert.Single(kernel.EndpointSessions.SnapshotForProcess(caller));
        Assert.Single(kernel.Processes.Resolve(caller).Value!.Channels);
        Assert.Single(kernel.Processes.Resolve(provider).Value!.Channels);
    }

    [Fact]
    public void ChannelRefusalDoesNotConsumeFinalSessionIdentity()
    {
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture();
        SetAllocator(kernel.EndpointSessions, "_nextId", ulong.MaxValue);
        SetAllocator(kernel.Channels, "_nextChannelId", 0);
        Assert.Equal(KernelError.CapacityExhausted, kernel.OpenSession(caller, descriptor, [capability]).Error);
        Assert.Empty(kernel.EndpointSessions.SnapshotForProcess(caller));
        Assert.Empty(kernel.Processes.Resolve(caller).Value!.Channels);
        SetAllocator(kernel.Channels, "_nextChannelId", 1);
        KernelResult<EndpointSessionHandle> final = kernel.OpenSession(caller, descriptor, [capability]);
        Assert.True(final.IsSuccess, final.Message);
        Assert.Equal(ulong.MaxValue, final.Value.SessionId.Value);
        Assert.Equal(KernelError.CapacityExhausted, kernel.OpenSession(caller, descriptor, [capability]).Error);
        Assert.Single(kernel.Processes.Resolve(provider).Value!.Channels);
    }

    private static void SetAllocator(object owner, string field, ulong value) => owner.GetType().GetField(field,
        global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.SetValue(owner, value);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OpeningCapabilityCaptureWorkerRevokeOrThrowRefusesBeforeAllocation(bool throws)
    {
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture();
        OpeningCapabilities inputs = new OpeningCapabilities(capability, () =>
        {
            Task<KernelResult> worker = Task.Run(() => kernel.RevokeCapability(capability));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Caller capture held authority locks.");
            Assert.True(worker.Result.IsSuccess);
            if (throws) throw new InvalidOperationException("Capture interrupted.");
        });
        Assert.False(kernel.OpenSession(caller, descriptor, inputs).IsSuccess);
        Assert.Empty(kernel.EndpointSessions.SnapshotForProcess(caller));
        Assert.Empty(kernel.Processes.Resolve(caller).Value!.Channels);
        Assert.Empty(kernel.Processes.Resolve(provider).Value!.Channels);
    }

    private sealed class OpeningCapabilities(CapabilityId capability, Action capture) : IReadOnlyCollection<CapabilityId>
    {
        public int Count => 1;
        public IEnumerator<CapabilityId> GetEnumerator()
        {
            capture();
            yield return capability;
        }
        global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void OpeningClockFaultOrOwnerLossCannotAllocateChannelOrSession(int fault)
    {
        TestTimeProvider clock = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(clock);
        int initialCallerChannels = kernel.Processes.Resolve(caller).Value!.Channels.Count;
        int initialProviderChannels = kernel.Processes.Resolve(provider).Value!.Channels.Count;
        clock.Callback = () =>
        {
            if (fault == 0) Assert.True(kernel.RevokeCapability(capability).IsSuccess);
            if (fault == 1) Assert.True(kernel.SetServiceAvailability(descriptor.Service, ServiceAvailability.Draining).IsSuccess);
            if (fault == 2) Assert.True(kernel.TerminateProcess(provider).IsSuccess);
            if (fault == 3) throw new InvalidOperationException("Opening clock failed.");
            if (fault == 4) Assert.True(kernel.TerminateProcess(caller).IsSuccess);
        };
        Assert.False(kernel.OpenSession(caller, descriptor, [capability], lifetime: TimeSpan.FromSeconds(1)).IsSuccess);
        Assert.Empty(kernel.EndpointSessions.SnapshotForProcess(caller));
        Assert.Empty(kernel.EndpointSessions.SnapshotForProcess(provider));
        if (fault != 4) Assert.Equal(initialCallerChannels, kernel.Processes.Resolve(caller).Value!.Channels.Count);
        if (fault != 2) Assert.Equal(initialProviderChannels, kernel.Processes.Resolve(provider).Value!.Channels.Count);
        if (fault == 3)
        {
            Assert.True(kernel.OpenSession(caller, descriptor, [capability], lifetime: TimeSpan.FromSeconds(1)).IsSuccess);
            Assert.Single(kernel.EndpointSessions.SnapshotForProcess(caller));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OpeningClockOrCaptureWorkerReentryRunsBeforeAnyOuterAllocation(bool capture)
    {
        TestTimeProvider clock = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(clock);
        Action callback = () =>
        {
            Assert.Empty(kernel.EndpointSessions.SnapshotForProcess(caller));
            Assert.Empty(kernel.Processes.Resolve(caller).Value!.Channels);
            Task<KernelResult<EndpointSessionHandle>> worker = Task.Run(() => kernel.OpenSession(caller, descriptor, [capability]));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Opening clock held process/service/capability/session lock.");
            Assert.True(worker.Result.IsSuccess, worker.Result.Message);
        };
        if (!capture) clock.Callback = callback;
        IReadOnlyCollection<CapabilityId> inputs = capture ? new OpeningCapabilities(capability, callback) : [capability];
        KernelResult<EndpointSessionHandle> opened = kernel.OpenSession(caller, descriptor, inputs, lifetime: TimeSpan.FromSeconds(1));
        Assert.True(opened.IsSuccess, opened.Message);
        Assert.Equal(2, kernel.EndpointSessions.SnapshotForProcess(caller).Length);
        Assert.Equal(2, kernel.Processes.Resolve(caller).Value!.Channels.Count);
        Assert.Equal(2, kernel.Processes.Resolve(provider).Value!.Channels.Count);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExpiryClockWorkerCloseOrResolveCannotDeadlockOrExpireTwice(bool resolve)
    {
        TestTimeProvider clock = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(clock);
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability], lifetime: TimeSpan.FromSeconds(1)).Value;
        ChannelEndpointHandle channel = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
        clock.Advance(TimeSpan.FromSeconds(2));
        clock.Callback = () =>
        {
            Task<KernelError> worker = Task.Run(() => resolve
                ? kernel.ResolveSession(caller, session).Error
                : kernel.CloseSession(caller, session).Error);
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Expiry clock held correlation/session owner lock.");
            Assert.Equal(resolve ? KernelError.SessionTimedOut : KernelError.None, worker.Result);
        };
        Assert.Equal(KernelError.SessionClosed, kernel.ResolveSession(caller, session).Error);
        SessionLifecycleTraceEventV1[] trace = kernel.EndpointSessions.SnapshotLifecycleTrace(session);
        Assert.Equal([EndpointSessionState.Active, EndpointSessionState.Closed], trace.Select(item => item.State));
        Assert.Equal(resolve ? SessionLifecycleTraceCauseV1.Expired : SessionLifecycleTraceCauseV1.ExplicitClose,
            trace[1].Cause);
        ChannelRegistry.ChannelClosureSnapshot closure = kernel.Channels.SnapshotClosure(channel)!.Value;
        Assert.True(closure.Closed);
        Assert.Equal(channel.Generation + 1, closure.CurrentGeneration);
        Assert.False(closure.AuthorizesReclaim);
        Assert.Equal(KernelError.SessionClosed, kernel.ReceiveSession(provider, session).Error);
    }

    [Fact]
    public void ExpiryClockFailurePreservesSessionAndTransportForExactRetry()
    {
        TestTimeProvider clock = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(clock);
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability], lifetime: TimeSpan.FromSeconds(1)).Value;
        ChannelEndpointHandle channel = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
        clock.Advance(TimeSpan.FromSeconds(2));
        clock.Callback = () => throw new InvalidOperationException("Expiry clock failed.");
        Assert.Equal(KernelError.PlatformFaulted, kernel.ResolveSession(caller, session).Error);
        Assert.Equal(EndpointSessionState.Active, kernel.EndpointSessions.Resolve(session, caller).Value!.State);
        Assert.False(kernel.Channels.SnapshotClosure(channel)!.Value.Closed);
        Assert.Single(kernel.EndpointSessions.SnapshotLifecycleTrace(session));
        Assert.Equal(KernelError.SessionTimedOut, kernel.ResolveSession(caller, session).Error);
        Assert.True(kernel.Channels.SnapshotClosure(channel)!.Value.Closed);
        Assert.Equal(KernelError.SessionClosed, kernel.ReceiveSession(provider, session).Error);
    }

    [Fact]
    public void ExpiryClockFreshPinDefersTransportCloseUntilActualSettlement()
    {
        TestTimeProvider clock = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(clock);
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability], lifetime: TimeSpan.FromSeconds(1)).Value;
        ChannelEndpointHandle channel = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
        TaskCompletionSource<InlineSipInvocationLease> acquired = new TaskCompletionSource<InlineSipInvocationLease>();
        clock.Advance(TimeSpan.FromSeconds(2));
        clock.Callback = () =>
        {
            Task<KernelResult<InlineSipInvocationLease>> worker = Task.Run(() => kernel.BeginInlineSessionInvocation(caller, provider, session, 1, null));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Expiry clock prevented fresh pin admission.");
            Assert.True(worker.Result.IsSuccess, worker.Result.Message);
            acquired.SetResult(worker.Result.Value!);
        };
        Assert.Equal(KernelError.SessionTimedOut, kernel.ResolveSession(caller, session).Error);
        Assert.False(kernel.Channels.SnapshotClosure(channel)!.Value.Closed);
        Assert.Equal(EndpointSessionState.Draining, kernel.EndpointSessions.SnapshotForProcess(caller).Single().State);
        Assert.True(kernel.SettleInlineSessionInvocation(provider, acquired.Task.Result, succeeded: false).IsSuccess);
        Assert.True(kernel.Channels.SnapshotClosure(channel)!.Value.Closed);
        Assert.Equal([EndpointSessionState.Active, EndpointSessionState.Draining, EndpointSessionState.Closed],
            kernel.EndpointSessions.SnapshotLifecycleTrace(session).Select(item => item.State));
    }

    [Fact]
    public async Task DiscoveredSessionInvokesAndClosesWithoutRawChannelOrProviderHandleAtCaller()
    {
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(withResponses: true);
        KernelResult<EndpointSessionHandle> session = kernel.OpenSession(caller, Assert.Single(kernel.ResolveByContract(descriptor.Contract).Value!), [capability]);
        Assert.True(session.IsSuccess, session.Message);

        RuntimeSipClientTransport transport = new RuntimeSipClientTransport(kernel, caller, session.Value);
        Task<ResponseEnvelope> invocation = transport.InvokeAsync(1).AsTask();
        KernelResult<ChannelEnvelope> request = kernel.ReceiveSession(provider, session.Value);
        Assert.True(request.IsSuccess, request.Message);
        KernelResult<ResponseEnvelope> publication = kernel.PublishSessionResponse(provider, session.Value, request.Value!.Sequence, 42);
        Assert.True(publication.IsSuccess, publication.Message);
        ResponseEnvelope response = await invocation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(42, response.Payload);
        Assert.True(kernel.CloseSession(caller, session.Value).IsSuccess);
    }

    [Fact]
    public void DiscoveryIsMetadataOnlyAndSessionIsOwnerAndGenerationBound()
    {
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture();
        KernelResult<ServiceEndpointDescriptor[]> found = kernel.ResolveByContract(descriptor.Contract);
        Assert.True(found.IsSuccess);
        Assert.Equal(descriptor, Assert.Single(found.Value!));
        Assert.Equal(descriptor, kernel.ResolveByServiceName("compute").Value);

        KernelResult<EndpointSessionHandle> opened = kernel.OpenSession(caller, descriptor, [capability]);
        Assert.True(opened.IsSuccess, opened.Message);

        KernelResult<EndpointSessionHandle> foreign = kernel.OpenSession(new ProcessHandle(new ProcessId(999), 1), descriptor, [capability]);
        Assert.False(foreign.IsSuccess);
        Assert.Equal(KernelError.ProcessNotFound, foreign.Error);

        KernelResult wrongOwner = kernel.CloseSession(provider, opened.Value);
        Assert.False(wrongOwner.IsSuccess);
        Assert.Equal(KernelError.WrongSessionOwner, wrongOwner.Error);
    }

    [Fact]
    public void MissingCapabilityAndStaleServiceGenerationFailBeforeChannelAdmission()
    {
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle _, ServiceEndpointDescriptor descriptor, CapabilityId _) = CreateFixture();
        KernelResult<EndpointSessionHandle> denied = kernel.OpenSession(caller, descriptor);
        Assert.False(denied.IsSuccess);
        Assert.Equal(KernelError.MissingCapability, denied.Error);

        KernelResult<EndpointSessionHandle> stale = kernel.OpenSession(caller, descriptor with { Generation = new ServiceGeneration(2) });
        Assert.False(stale.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, stale.Error);

        KernelResult<EndpointSessionHandle> wrongContract = kernel.OpenSession(caller, descriptor with { Contract = descriptor.Contract with { Version = "2" } });
        Assert.False(wrongContract.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, wrongContract.Error);
    }

    [Fact]
    public void RevokeCloseDrainAndDoubleCloseAreFailClosed()
    {
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle _, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture();
        KernelResult<EndpointSessionHandle> session = kernel.OpenSession(caller, descriptor, [capability]);
        Assert.True(session.IsSuccess, session.Message);

        Assert.True(kernel.SetServiceAvailability(descriptor.Service, ServiceAvailability.Draining).IsSuccess);
        KernelResult<EndpointSessionHandle> rejected = kernel.OpenSession(caller, descriptor, [capability]);
        Assert.False(rejected.IsSuccess);
        Assert.Equal(KernelError.ServiceUnavailable, rejected.Error);

        Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        RuntimeSipClientTransport transport = new RuntimeSipClientTransport(kernel, caller, session.Value);
        InvalidOperationException invocation = Assert.Throws<InvalidOperationException>(() => transport.Invoke(1));
        Assert.Contains(nameof(KernelError.CapabilityRevoked), invocation.Message, StringComparison.Ordinal);
        Assert.True(kernel.CloseSession(caller, session.Value).IsSuccess);
    }

    [Fact]
    public void CloseIsIdempotenceSensitiveAndStaleSessionCannotReplay()
    {
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle _, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture();
        KernelResult<EndpointSessionHandle> session = kernel.OpenSession(caller, descriptor, [capability]);
        Assert.True(session.IsSuccess, session.Message);
        Assert.True(kernel.CloseSession(caller, session.Value).IsSuccess);

        KernelResult replay = kernel.CloseSession(caller, session.Value);
        Assert.False(replay.IsSuccess);
        Assert.Equal(KernelError.SessionClosed, replay.Error);
        KernelResult stale = kernel.CloseSession(caller, session.Value with { Generation = new EndpointSessionGeneration(2) });
        Assert.False(stale.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, stale.Error);
    }

    [Fact]
    public async Task ConcurrentCloseHasOneWinnerAndNeverReopensAuthority()
    {
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle _, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture();
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        KernelResult[] closes = await Task.WhenAll(
            Task.Run(() => kernel.CloseSession(caller, session)),
            Task.Run(() => kernel.CloseSession(caller, session)));
        Assert.Single(closes, x => x.IsSuccess);
        Assert.Single(closes, x => x.Error == KernelError.SessionClosed);
    }

    [Fact]
    public async Task ConcurrentOpenAndProviderTeardownLeaveNoActiveSessionOrOpenChannel()
    {
        for (int iteration = 0; iteration < 32; iteration++)
        {
            (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture();
            TaskCompletionSource<bool> start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<KernelResult<EndpointSessionHandle>> opening = Task.Run(async () =>
            {
                await start.Task;
                return kernel.OpenSession(caller, descriptor, [capability]);
            });
            Task<KernelResult> teardown = Task.Run(async () =>
            {
                await start.Task;
                return kernel.TerminateProcess(provider);
            });
            start.SetResult(true);
            await Task.WhenAll(opening, teardown).WaitAsync(TimeSpan.FromSeconds(5));

            KernelResult closed = await teardown;
            Assert.True(closed.IsSuccess, closed.Message);
            Assert.DoesNotContain(kernel.EndpointSessions.SnapshotForProcess(caller),
                item => item.State == EndpointSessionState.Active);
            Assert.Equal(0, kernel.Channels.InspectionSummary(caller).Channels);
            KernelResult<EndpointSessionHandle> opened = await opening;
            if (opened.IsSuccess)
            {
                KernelResult<EndpointSessionRegistry.Record> session = kernel.EndpointSessions.Resolve(opened.Value, caller);
                Assert.Equal(KernelError.SessionClosed, session.Error);
            }
        }
    }

    [Fact]
    public void ProviderDeathClosesSessionsAndRemovesServiceFromDiscovery()
    {
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture();
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        Assert.True(kernel.TerminateProcess(provider).IsSuccess);
        Assert.Empty(kernel.ResolveByContract(descriptor.Contract).Value!);
        Assert.Equal(KernelError.SessionClosed, kernel.CloseSession(caller, session).Error);
    }

    [Fact]
    public async Task ConcurrentServiceRegistrationAndProviderTeardownLeaveNoDiscoverableDescriptor()
    {
        for (int iteration = 0; iteration < 32; iteration++)
        {
            RuntimeKernel kernel = new RuntimeKernel();
            ProcessHandle provider = TestFixtures.Create(kernel, 5, 50).Handle;
            Assert.True(kernel.AdmitProcess(provider).IsSuccess);
            ServiceContractIdentity contract = new ServiceContractIdentity("RaceService", "1", "race-digest");
            ProtocolDefinitionV1 protocol = new ProtocolDefinitionV1("RaceService", "race-digest", "Idle", null,
                [new ProtocolMessageDescriptorV1(1, "Run")], [new ProtocolTransitionV1(1, "Idle", "Idle")]);
            TaskCompletionSource<bool> start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<KernelResult<ServiceEndpointDescriptor>> registration = Task.Run(async () =>
            {
                await start.Task;
                return kernel.RegisterService(provider, "race-service", contract, protocol);
            });
            Task<KernelResult> teardown = Task.Run(async () =>
            {
                await start.Task;
                return kernel.TerminateProcess(provider);
            });
            start.SetResult(true);
            await Task.WhenAll(registration, teardown).WaitAsync(TimeSpan.FromSeconds(5));

            KernelResult closed = await teardown;
            Assert.True(closed.IsSuccess, closed.Message);
            Assert.Empty(kernel.ResolveByContract(contract).Value!);
        }
    }

    [Fact]
    public void ExpiredAndStaleSessionFailBeforeProtocolMutation()
    {
        TestTimeProvider clock = new TestTimeProvider(new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(clock);
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability], lifetime: TimeSpan.FromSeconds(1)).Value;
        clock.Advance(TimeSpan.FromSeconds(2));
        RuntimeSipClientTransport transport = new RuntimeSipClientTransport(kernel, caller, session);
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => transport.Invoke(1));
        Assert.Contains(nameof(KernelError.SessionTimedOut), failure.Message, StringComparison.Ordinal);
        Assert.Equal(KernelError.SessionClosed, kernel.ReceiveSession(provider, session).Error);
    }

    [Fact]
    public void ExpirationWithInlineEffectPinDefersChannelCloseUntilSettlement()
    {
        TestTimeProvider clock = new TestTimeProvider(new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));
        (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle provider, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture(clock);
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability], lifetime: TimeSpan.FromSeconds(1)).Value;
        ChannelEndpointHandle endpoint = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
        KernelResult<InlineSipInvocationLease> begun = kernel.BeginInlineSessionInvocation(caller, provider, session, 1, null);
        Assert.True(begun.IsSuccess, begun.Message);

        clock.Advance(TimeSpan.FromSeconds(2));
        KernelResult<EndpointSessionRegistry.Record> expired = kernel.ResolveSession(caller, session);
        Assert.Equal(KernelError.SessionTimedOut, expired.Error);
        Assert.Equal(EndpointSessionState.Draining, kernel.EndpointSessions.SnapshotForProcess(caller).Single().State);
        Assert.True(kernel.Channels.GetEndpoint(endpoint).IsSuccess);
        Assert.False(kernel.RevalidateInlineSessionInvocation(begun.Value!).IsSuccess);

        Assert.True(kernel.SettleInlineSessionInvocation(provider, begun.Value!, succeeded: false).IsSuccess);
        Assert.Equal(EndpointSessionState.Closed, kernel.EndpointSessions.SnapshotForProcess(caller).Single().State);
        Assert.Equal(KernelError.StaleGeneration, kernel.Channels.GetEndpoint(endpoint).Error);
    }

    [Fact]
    public void ExplicitCloseAndExpirationHaveTheSameObservedOwnerStateProjection()
    {
        (RuntimeKernel? referenceKernel, ProcessHandle referenceCaller, ProcessHandle referenceService, ServiceEndpointDescriptor referenceDescriptor, CapabilityId referenceCapability) = CreateFixture();
        EndpointSessionHandle referenceSession = referenceKernel.OpenSession(referenceCaller, referenceDescriptor,
            [referenceCapability]).Value;
        ChannelEndpointHandle referenceChannel = referenceKernel.EndpointSessions.Resolve(
            referenceSession, referenceCaller).Value!.Channel;
        InlineSipInvocationLease referencePin = referenceKernel.BeginInlineSessionInvocation(
            referenceCaller, referenceService, referenceSession, 1, null).Value!;
        Assert.Equal(KernelError.SessionDraining,
            referenceKernel.CloseSession(referenceCaller, referenceSession).Error);
        Assert.False(referenceKernel.Channels.SnapshotClosure(referenceChannel)!.Value.Closed);
        Assert.True(referenceKernel.SettleInlineSessionInvocation(
            referenceService, referencePin, succeeded: true).IsSuccess);
        ChannelRegistry.ChannelClosureSnapshot referenceClosure = referenceKernel.Channels.SnapshotClosure(referenceChannel)!.Value;
        Assert.True(referenceClosure.Closed);
        Assert.Equal(referenceChannel.Generation + 1, referenceClosure.CurrentGeneration);
        Assert.False(referenceClosure.AuthorizesReclaim);
        SessionLifecycleTraceEventV1[] reference = referenceKernel.EndpointSessions.SnapshotLifecycleTrace(referenceSession);

        TestTimeProvider clock = new TestTimeProvider(new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));
        (RuntimeKernel? candidateKernel, ProcessHandle candidateCaller, ProcessHandle candidateService, ServiceEndpointDescriptor candidateDescriptor, CapabilityId candidateCapability) =
            CreateFixture(clock);
        EndpointSessionHandle candidateSession = candidateKernel.OpenSession(candidateCaller, candidateDescriptor,
            [candidateCapability], lifetime: TimeSpan.FromSeconds(1)).Value;
        ChannelEndpointHandle candidateChannel = candidateKernel.EndpointSessions.Resolve(
            candidateSession, candidateCaller).Value!.Channel;
        InlineSipInvocationLease candidatePin = candidateKernel.BeginInlineSessionInvocation(
            candidateCaller, candidateService, candidateSession, 1, null).Value!;
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(KernelError.SessionTimedOut,
            candidateKernel.ResolveSession(candidateCaller, candidateSession).Error);
        Assert.False(candidateKernel.Channels.SnapshotClosure(candidateChannel)!.Value.Closed);
        Assert.True(candidateKernel.SettleInlineSessionInvocation(
            candidateService, candidatePin, succeeded: true).IsSuccess);
        Assert.True(candidateKernel.Channels.SnapshotClosure(candidateChannel)!.Value.Closed);
        Assert.Null(candidateKernel.Channels.SnapshotClosure(candidateChannel with
        { Generation = candidateChannel.Generation + 1 }));
        Assert.Null(candidateKernel.Channels.SnapshotClosure(candidateChannel with
        { EndpointId = new EndpointId(3) }));
        SessionLifecycleTraceEventV1[] candidate = candidateKernel.EndpointSessions.SnapshotLifecycleTrace(candidateSession);

        Assert.Equal([EndpointSessionState.Active, EndpointSessionState.Draining,
            EndpointSessionState.Closed], reference.Select(item => item.State));
        Assert.Equal([SessionLifecycleTraceCauseV1.Opened, SessionLifecycleTraceCauseV1.Expired,
            SessionLifecycleTraceCauseV1.PinReleased], candidate.Select(item => item.Cause));
        Assert.True(SessionLifecycleTraceValidatorV1.Validate(reference).IsValid);
        Assert.True(SessionLifecycleTraceValidatorV1.Validate(candidate).IsValid);
        Assert.True(SessionLifecycleTraceDifferentialV1.CompareAllowedStates(reference, candidate,
            new string('a', 64), new string('b', 64)).IsEquivalent);
        Assert.All(reference.Concat(candidate), item => Assert.False(item.AuthorizesClosure));
    }

    [Fact]
    public async Task ConcurrentCloseAndProcessTeardownRecordOneDrainingTransition()
    {
        for (int iteration = 0; iteration < 16; iteration++)
        {
            (RuntimeKernel? kernel, ProcessHandle caller, ProcessHandle service, ServiceEndpointDescriptor descriptor, CapabilityId capability) = CreateFixture();
            EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
            ChannelEndpointHandle channel = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
            InlineSipInvocationLease pin = kernel.BeginInlineSessionInvocation(caller, service, session, 1, null).Value!;
            TaskCompletionSource<bool> start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<KernelResult> closing = Task.Run(async () =>
            {
                await start.Task;
                return kernel.CloseSession(caller, session);
            });
            Task<KernelResult> teardown = Task.Run(async () =>
            {
                await start.Task;
                return kernel.TerminateProcess(service);
            });
            start.SetResult(true);
            await Task.WhenAll(closing, teardown).WaitAsync(TimeSpan.FromSeconds(10));
            KernelResult closeResult = await closing;
            KernelResult teardownResult = await teardown;
            Assert.Equal(KernelError.PlatformBindingDraining, teardownResult.Error);
            Assert.True(closeResult.IsSuccess || closeResult.Error is KernelError.SessionDraining or
                KernelError.SessionClosed);
            SessionLifecycleTraceEventV1[] prefix = kernel.EndpointSessions.SnapshotLifecycleTrace(session);
            Assert.Equal([EndpointSessionState.Active, EndpointSessionState.Draining],
                prefix.Select(item => item.State));
            Assert.True(kernel.Channels.SnapshotClosure(channel)!.Value.Closed);
            Assert.True(SessionLifecycleTraceValidatorV1.Validate(prefix).IsValid);

            _ = kernel.SettleInlineSessionInvocation(service, pin, succeeded: false);
            SessionLifecycleTraceEventV1[] final = kernel.EndpointSessions.SnapshotLifecycleTrace(session);
            Assert.Equal([EndpointSessionState.Active, EndpointSessionState.Draining,
                EndpointSessionState.Closed], final.Select(item => item.State));
            Assert.True(SessionLifecycleTraceValidatorV1.Validate(final).IsValid);
        }
    }

    [Fact]
    public void DiscoveryDescriptorContainsNoInvocationAuthorityTypes()
    {
        Type[] propertyTypes = typeof(ServiceEndpointDescriptor).GetProperties().Select(x => x.PropertyType).ToArray();
        Assert.DoesNotContain(typeof(ProcessHandle), propertyTypes);
        Assert.DoesNotContain(typeof(ChannelEndpointHandle), propertyTypes);
        Assert.DoesNotContain(typeof(CapabilityId), propertyTypes);
        Assert.NotEqual(typeof(ServiceId), typeof(EndpointSessionId));
    }

    private static (RuntimeKernel Kernel, ProcessHandle Caller, ProcessHandle Provider, ServiceEndpointDescriptor Descriptor, CapabilityId Capability) CreateFixture(TimeProvider? timeProvider = null, bool withResponses = false, bool bounded = false)
    {
        RuntimeKernel kernel = new RuntimeKernel(null, timeProvider);
        ProcessHandle caller = TestFixtures.Create(kernel, 1, 10).Handle;
        ProcessHandle provider = TestFixtures.Create(kernel, 2, 20).Handle;
        Assert.True(kernel.AdmitProcess(caller).IsSuccess);
        Assert.True(kernel.AdmitProcess(provider).IsSuccess);
        CapabilityId capability = kernel.MintCapability(new DomainId(10), caller, ResourceKind.KernelService, "compute", CapabilityRights.Read).Value!.CapabilityId;
        ProtocolDefinitionV1 protocol = new ProtocolDefinitionV1("Compute", "digest", "Idle", ["Done"], [new ProtocolMessageDescriptorV1(1, "Run", requestPayload: bounded ? new(RequestPayloadKind.Bounded, "payload", typeof(CallbackPayload).FullName, maxBytes: 16) : null)], [new ProtocolTransitionV1(1, "Idle", "Done")]);
        ServiceContractIdentity contract = new ServiceContractIdentity("Compute", "1", "digest");
        ResponseProtocolDefinitionV1? responses = withResponses
            ? new ResponseProtocolDefinitionV1("Compute", "response-digest", [new ResponseMessageDescriptorV1(1, "Run", new ResponsePayloadDescriptorV1(ResponsePayloadKind.Primitive, typeof(int).FullName))])
            : null;
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(provider, "compute", contract, protocol, responses, [new CapabilityRequirementV1(ResourceKind.KernelService, "compute", CapabilityRights.Read)]).Value!;
        return (kernel, caller, provider, descriptor, capability);
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        internal Action? Callback;
        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Exchange(ref Callback, null)?.Invoke();
            return _now;
        }
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
