using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private readonly object _requestResponseCorrelationGate = new();

    public KernelResult<(ChannelEndpointHandle Left, ChannelEndpointHandle Right)> CreateChannel(
        ProcessHandle left,
        ProcessHandle right,
        ProtocolDefinitionV1 protocol,
        int capacity) =>
        CreateChannelCore(left, right, protocol, responseProtocol: null, capacity);

    public KernelResult<(ChannelEndpointHandle Left, ChannelEndpointHandle Right)> CreateChannel(
        ProcessHandle left,
        ProcessHandle right,
        ProtocolDefinitionV1 protocol,
        ResponseProtocolDefinitionV1 responseProtocol,
        int capacity) =>
        CreateChannelCore(left, right, protocol, responseProtocol, capacity);

    private KernelResult<(ChannelEndpointHandle Left, ChannelEndpointHandle Right)> CreateChannelCore(
        ProcessHandle left,
        ProcessHandle right,
        ProtocolDefinitionV1 protocol,
        ResponseProtocolDefinitionV1? responseProtocol,
        int capacity)
    {
        lock (_platformMemoryUseGate)
        {
            KernelResult<SingProcess> leftProcess = Processes.Resolve(left);
        if (!leftProcess.IsSuccess)
            return KernelResult<(ChannelEndpointHandle, ChannelEndpointHandle)>.Fail(leftProcess.Error, leftProcess.Message!);
            KernelResult leftEffect = EnsureProcessAcceptsNewEffects(leftProcess.Value!);
        if (!leftEffect.IsSuccess)
            return KernelResult<(ChannelEndpointHandle, ChannelEndpointHandle)>.Fail(leftEffect.Error, leftEffect.Message!);

            KernelResult<SingProcess> rightProcess = Processes.Resolve(right);
        if (!rightProcess.IsSuccess)
            return KernelResult<(ChannelEndpointHandle, ChannelEndpointHandle)>.Fail(rightProcess.Error, rightProcess.Message!);
            KernelResult rightEffect = EnsureProcessAcceptsNewEffects(rightProcess.Value!);
        if (!rightEffect.IsSuccess)
            return KernelResult<(ChannelEndpointHandle, ChannelEndpointHandle)>.Fail(rightEffect.Error, rightEffect.Message!);

        if (leftProcess.Value!.Channels.Count >= leftProcess.Value.Manifest.ResourceLimits.MaxChannels ||
            rightProcess.Value!.Channels.Count >= rightProcess.Value.Manifest.ResourceLimits.MaxChannels)
        {
            return KernelResult<(ChannelEndpointHandle, ChannelEndpointHandle)>.Fail(
                KernelError.CapacityExhausted,
                "Channel resource limit exceeded.");
        }

        if (responseProtocol is not null)
        {
                KernelResult responseValidation = ResponseRegistry.ValidateProtocol(protocol, responseProtocol);
            if (!responseValidation.IsSuccess)
            {
                return KernelResult<(ChannelEndpointHandle, ChannelEndpointHandle)>.Fail(
                    responseValidation.Error,
                    responseValidation.Message!);
            }
        }

            KernelResult<(ChannelEndpointHandle Left, ChannelEndpointHandle Right)> created = Channels.Create(protocol, leftProcess.Value, rightProcess.Value, capacity);
        if (!created.IsSuccess) return created;
            (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints = created.Value;
        if (responseProtocol is not null)
        {
            Responses.RegisterChannel(
                endpoints.Left,
                endpoints.Right,
                responseProtocol,
                left,
                right,
                capacity);
        }

        leftProcess.Value.AddChannel(endpoints.Left);
        rightProcess.Value.AddChannel(endpoints.Right);
        return KernelResult<(ChannelEndpointHandle, ChannelEndpointHandle)>.Ok(endpoints);
        }
    }

    public KernelResult<ChannelEnvelope> Send(
        ProcessHandle sender,
        ProcessHandle receiver,
        ChannelEndpointHandle endpoint,
        uint messageId,
        object? payload = null,
        IReadOnlyCollection<CapabilityId>? capabilities = null,
        AdmissionQosHint qosHint = AdmissionQosHint.None,
        TraceCausalContext? traceContext = null) =>
        SendCore(sender, receiver, endpoint, messageId, payload, secondaryPayload: null, capabilities, qosHint, traceContext);

    public KernelResult<ChannelEnvelope> SendOwnershipPair(
        ProcessHandle sender,
        ProcessHandle receiver,
        ChannelEndpointHandle endpoint,
        uint messageId,
        object firstOwnershipPayload,
        object secondOwnershipPayload,
        IReadOnlyCollection<CapabilityId>? capabilities = null,
        AdmissionQosHint qosHint = AdmissionQosHint.None,
        TraceCausalContext? traceContext = null) =>
        SendCore(sender, receiver, endpoint, messageId, firstOwnershipPayload, secondOwnershipPayload, capabilities, qosHint, traceContext);

    private sealed record SessionSendPublication(EndpointSessionHandle Session,
        EndpointSessionPin Pin, CapabilityRequirementV1[] Requirements, CancellationScopeHandle? Scope = null,OriginalSessionSendCustody? Custody=null);

    private KernelResult<ChannelEnvelope> SendCore(
        ProcessHandle sender,
        ProcessHandle receiver,
        ChannelEndpointHandle endpoint,
        uint messageId,
        object? payload,
        object? secondaryPayload,
        IReadOnlyCollection<CapabilityId>? capabilities,
        AdmissionQosHint qosHint,
        TraceCausalContext? traceContext,
        SessionSendPublication? sessionPublication = null)
    {
        CapabilityId[]? capabilitySnapshot = capabilities?.ToArray();
        ChannelRegistry.BoundedPayloadSnapshot? boundedSnapshot = Channels.CaptureInlineCopiedPayload(endpoint, messageId, payload);
        ulong estimatedBytes = boundedSnapshot is { } bounded && secondaryPayload is null
            ? checked(32UL + (ulong)Math.Max(0, bounded.Size))
            : EstimateIpcBytes(payload, secondaryPayload);
        List<BudgetTraceObservation> observations = [];
        KernelResult<ChannelEnvelope> committed;
        try { committed = Commit(); }
        finally { EmitBudgetObservations(observations); }
        if (!committed.IsSuccess) return committed;
        RecordTrace(sender, TraceEventKind.IpcSent, traceContext, "ipc",
            $"{endpoint.ChannelId.Value}:{committed.Value!.Sequence}", "queued", "admitted");
        return committed;

        KernelResult<ChannelEnvelope> Commit()
        {
        lock (_platformMemoryUseGate)
        {
                KernelResult<SingProcess> senderProcess = Processes.Resolve(sender);
        if (!senderProcess.IsSuccess)
            return KernelResult<ChannelEnvelope>.Fail(senderProcess.Error, senderProcess.Message!);
                KernelResult senderEffect = EnsureProcessAcceptsNewEffects(senderProcess.Value!);
        if (!senderEffect.IsSuccess)
            return KernelResult<ChannelEnvelope>.Fail(senderEffect.Error, senderEffect.Message!);
                KernelResult<SingProcess> receiverProcess = Processes.Resolve(receiver);
        if (!receiverProcess.IsSuccess)
            return KernelResult<ChannelEnvelope>.Fail(receiverProcess.Error, receiverProcess.Message!);
                KernelResult receiverEffect = EnsureProcessAcceptsNewEffects(receiverProcess.Value!);
        if (!receiverEffect.IsSuccess)
            return KernelResult<ChannelEnvelope>.Fail(receiverEffect.Error, receiverEffect.Message!);

        lock (_requestResponseCorrelationGate)
        {
            if (sessionPublication is { } pendingSession)
            {
                        KernelResult pin = EndpointSessions.RevalidatePin(pendingSession.Pin);
                if (!pin.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(pin.Error, pin.Message!);
            }
                    KernelResult responsePreflight = Responses.CanRegisterRequest(endpoint, messageId, sender, receiver);
            if (!responsePreflight.IsSuccess)
                return KernelResult<ChannelEnvelope>.Fail(responsePreflight.Error, responsePreflight.Message!);

            // Ownership implementations are sealed TCB-local buffer/region operations;
            // their transfer observations are deferred past this owner commit.
            if (sessionPublication is { } copiedSession)
                return CapabilityAuthority.CommitSessionPublication(senderProcess.Value!.DomainId,
                    sender.Generation, capabilitySnapshot ?? [], copiedSession.Requirements, SendAndPublish);
            return SendAndPublish();

            KernelResult<ChannelEnvelope> SendAndPublish()
            {

                        KernelResult<BudgetReservationHandle?> budget = ReserveAttachedBudget(sender,
                [
                    new(ServiceBudgetDimension.IpcMessages, 1),
                    new(ServiceBudgetDimension.IpcBytes, estimatedBytes),
                ],
                BudgetReservationLifetime.IpcQueued,
                qosHint, observations);
            if (!budget.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(budget.Error, budget.Message!);

            if(budget.Value is { } originalBudget)
            {
                            KernelResult retained =Channels.RetainSendBudget(endpoint,sender,originalBudget);
                if(!retained.IsSuccess)
                {
                    _=ReleaseAttachedBudget(sender,originalBudget,observations);
                    return KernelResult<ChannelEnvelope>.Fail(retained.Error,retained.Message!);
                }
            }
            if(sessionPublication is { } originalSession)
            {
                            KernelResult retainedPin =EndpointSessions.RetainOriginalSendPin(originalSession.Pin);
                if(!retainedPin.IsSuccess)
                {
                    _=ReleaseAttachedBudget(sender,budget.Value,observations);
                    if(budget.Value is { } refused) Channels.ConsumeSendBudgetCorrelation(endpoint,sender,refused);
                    return KernelResult<ChannelEnvelope>.Fail(retainedPin.Error,retainedPin.Message!);
                }
            }
            EndpointSessionInvocationRegistry.OriginalSend? originalSend=null;
            ulong originalSequence=0;
            if(sessionPublication is { } preparedSession)
            {
                            KernelResult<ChannelEndpoint> channel =Channels.GetEndpoint(endpoint);
                if(!channel.IsSuccess || channel.Value!.Sequence==ulong.MaxValue)
                {
                    _=ReleaseAttachedBudget(sender,budget.Value,observations);
                    if(budget.Value is { } refused) Channels.ConsumeSendBudgetCorrelation(endpoint,sender,refused);
                    EndpointSessions.ConsumeOriginalSendPinCorrelation(preparedSession.Pin);
                    return KernelResult<ChannelEnvelope>.Fail(KernelError.CapacityExhausted,"Original session send sequence unavailable.");
                }
                originalSequence=channel.Value.Sequence+1;
                originalSend=SessionInvocations.PrepareOriginalSend(preparedSession.Session,sender,receiver,
                    originalSequence,messageId,preparedSession.Pin,payload,secondaryPayload,preparedSession.Scope);
                preparedSession.Custody?.Retain(this,sender,preparedSession.Session,originalSend);
                if(budget.Value is { } originalReservation)
                {
                                KernelResult<object> lifetime =Channels.PrepareOriginalQueuedLifetime(endpoint);
                    if(!lifetime.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(lifetime.Error,lifetime.Message!);
                                KernelResult<object> bound =Budgets.BindOriginalQueuedCharge(sender,receiver,originalReservation,endpoint.ChannelId,originalSequence,lifetime.Value!);
                    if(!bound.IsSuccess || !Channels.BindOriginalQueuedBudgetOwner(lifetime.Value!,bound.Value!))
                        return KernelResult<ChannelEnvelope>.Fail(KernelError.PlatformFaulted,"Original queued charge preparation failed.");
                    Channels.CaptureQueuedLifetime(endpoint.ChannelId,originalSequence);
                }
            }
            try
            {
            KernelResult<RegionHandle> TransferOriginal(SingProcess source,SingProcess target,RegionHandle region,Func<KernelResult>? admission,Action noEffect,PreparedOwnedTransfer prepared)
            {
                if(originalSend is not null && !SessionInvocations.BindOriginalSendTransfer(originalSend,prepared))
                    return KernelResult<RegionHandle>.Fail(KernelError.PlatformFaulted,"Original transfer preparation cannot bind.");
                                KernelResult<RegionHandle> result =TransferRegionForIpcCore(source,target,region,admission,observations,noEffect,prepared,originalSend);
                if(originalSend is not null) SessionInvocations.RetainOriginalSendTransferResult(originalSend,prepared,result);
                return result;
            }
                            KernelResult<ChannelEnvelope> send = secondaryPayload is null
                ? Channels.Send(
                    senderProcess.Value!,
                    receiverProcess.Value!,
                    endpoint,
                    messageId,
                    payload,
                    capabilitySnapshot,
                    boundedSnapshot,
                    TransferOriginal)
                : Channels.SendOwnershipPair(
                    senderProcess.Value!,
                    receiverProcess.Value!,
                    endpoint,
                    messageId,
                    payload!,
                    secondaryPayload,
                    capabilitySnapshot,
                    TransferOriginal);
            if (!send.IsSuccess)
            {
                if(Channels.SendOwnershipIsPending(endpoint))
                {
                    if(budget.Value is { } uncertain) _=Budgets.QuarantineLease(sender,uncertain);
                }
                else
                {
                    Channels.EndRefusedQueuedPreparation(endpoint);
                    _ = ReleaseAttachedBudget(sender,budget.Value,observations);
                    if(budget.Value is { } refused) Channels.ConsumeSendBudgetCorrelation(endpoint,sender,refused);
                    if(originalSend is not null && (!Channels.RetireUnpublishedSequence(endpoint,originalSequence) || !SessionInvocations.CompleteOriginalSend(originalSend,null)))
                        return KernelResult<ChannelEnvelope>.Fail(KernelError.PlatformFaulted,"Original no-effect send identity could not retire.");
                    if(sessionPublication is { } noEffectSession) EndpointSessions.ConsumeOriginalSendPinCorrelation(noEffectSession.Pin);
                }
                return send;
            }

            // A response-capable request must not become observable by the receiver
            // until its exact response correlation is registered. Receive uses the
            // same gate, so enqueue + correlation publication are one visibility step.
            if(originalSend is not null && !SessionInvocations.CompleteOriginalSend(originalSend,send.Value!,false))
                return KernelResult<ChannelEnvelope>.Fail(KernelError.PlatformFaulted,"Original send raw continuity failed.");
            Responses.RegisterRequest(endpoint, send.Value!, sender, receiver);
            if (sessionPublication is { } publication)
                SessionInvocations.Register(publication.Session, sender, receiver,
                    send.Value!.Sequence, messageId, publication.Scope);
            if (budget.Value is { } reservation)
            {
                _ipcBudgetReservations.Add((endpoint.ChannelId, send.Value!.Sequence), (sender, receiver, reservation));
                                object? lifetime = Channels.CaptureQueuedLifetime(endpoint.ChannelId, send.Value.Sequence);
                if (lifetime is null) return KernelResult<ChannelEnvelope>.Fail(KernelError.PlatformFaulted, "Original queued lifetime is missing after enqueue.");
                                KernelResult<object> bound = Budgets.BindOriginalQueuedCharge(sender, receiver, reservation, endpoint.ChannelId, send.Value.Sequence, lifetime);
                if (!bound.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(bound.Error, bound.Message!);
                if (!Channels.BindOriginalQueuedBudgetOwner(lifetime, bound.Value!))
                    return KernelResult<ChannelEnvelope>.Fail(KernelError.PlatformFaulted, "Original queued budget owner continuity was lost after enqueue.");
            }
            if(originalSend is not null && !SessionInvocations.CompleteOriginalSend(originalSend,send.Value!))
                return KernelResult<ChannelEnvelope>.Fail(KernelError.PlatformFaulted,"Original send final publication failed.");
            if(budget.Value is { } published) Channels.ConsumeSendBudgetCorrelation(endpoint,sender,published);
            if(sessionPublication is { } publishedSession) EndpointSessions.ConsumeOriginalSendPinCorrelation(publishedSession.Pin);
            return send;
            }
            catch
            {
                if(budget.Value is { } uncertain) _=Budgets.QuarantineLease(sender,uncertain);
                throw;
            }
            }
        }
        }
        }
    }

    public KernelResult<ChannelEnvelope> Receive(ProcessHandle receiver, ChannelEndpointHandle endpoint)
    {
        KernelResult<ChannelEnvelope> committed = ReceiveCommitted(receiver, endpoint);
        if (!committed.IsSuccess) return committed;
        ObserveReceived(receiver, endpoint, committed.Value!);
        return committed;
    }

    private void ObserveReceived(ProcessHandle receiver, ChannelEndpointHandle endpoint, ChannelEnvelope envelope)
    {
        ReleaseIpcBudget((endpoint.ChannelId, envelope.Sequence));
        RecordTrace(receiver, TraceEventKind.IpcReceived, null, "ipc",
            $"{endpoint.ChannelId.Value}:{envelope.Sequence}", "received", "delivered");
    }

    // Trusted local transport/delivery commit only; callers emit observations after all outer locks.
    private KernelResult<ChannelEnvelope> ReceiveCommitted(ProcessHandle receiver, ChannelEndpointHandle endpoint)
    {
        lock (_platformMemoryUseGate)
        {
            KernelResult<SingProcess> process = Processes.Resolve(receiver);
        if (!process.IsSuccess)
            return KernelResult<ChannelEnvelope>.Fail(process.Error, process.Message!);

        lock (_requestResponseCorrelationGate)
        {
                KernelResult<ChannelEnvelope> next = Channels.Peek(process.Value!, endpoint);
            if (!next.IsSuccess) return next;
                KernelResult delivery = Responses.CanMarkDelivered(endpoint, next.Value!, receiver);
            if (!delivery.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(delivery.Error, delivery.Message!);
                KernelResult<ChannelEnvelope> receive = Channels.Receive(process.Value!, endpoint);
            if (!receive.IsSuccess) return receive;

            Responses.MarkDelivered(endpoint, receive.Value!, receiver);
            return receive;
        }
        }
    }

    private static ulong EstimateIpcBytes(object? payload, object? secondaryPayload)
    {
        static ulong One(object? value) => value switch
        {
            null => 0,
            IBoundedPayload bounded => checked((ulong)Math.Max(0, bounded.PayloadSize)),
            string text => checked((ulong)text.Length * 2),
            byte[] bytes => checked((ulong)bytes.Length),
            _ when value.GetType().IsPrimitive || value is Enum => 16,
            _ => 64,
        };
        return checked(32UL + One(payload) + One(secondaryPayload));
    }
}




