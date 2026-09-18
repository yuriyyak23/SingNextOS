using SingPlus.Contracts;

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
        var leftProcess = Processes.Resolve(left);
        if (!leftProcess.IsSuccess)
            return KernelResult<(ChannelEndpointHandle, ChannelEndpointHandle)>.Fail(leftProcess.Error, leftProcess.Message!);
        var leftEffect = EnsureProcessAcceptsNewEffects(leftProcess.Value!);
        if (!leftEffect.IsSuccess)
            return KernelResult<(ChannelEndpointHandle, ChannelEndpointHandle)>.Fail(leftEffect.Error, leftEffect.Message!);

        var rightProcess = Processes.Resolve(right);
        if (!rightProcess.IsSuccess)
            return KernelResult<(ChannelEndpointHandle, ChannelEndpointHandle)>.Fail(rightProcess.Error, rightProcess.Message!);
        var rightEffect = EnsureProcessAcceptsNewEffects(rightProcess.Value!);
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
            var responseValidation = ResponseRegistry.ValidateProtocol(protocol, responseProtocol);
            if (!responseValidation.IsSuccess)
            {
                return KernelResult<(ChannelEndpointHandle, ChannelEndpointHandle)>.Fail(
                    responseValidation.Error,
                    responseValidation.Message!);
            }
        }

        var endpoints = Channels.Create(protocol, leftProcess.Value, rightProcess.Value, capacity);
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

    private KernelResult<ChannelEnvelope> SendCore(
        ProcessHandle sender,
        ProcessHandle receiver,
        ChannelEndpointHandle endpoint,
        uint messageId,
        object? payload,
        object? secondaryPayload,
        IReadOnlyCollection<CapabilityId>? capabilities,
        AdmissionQosHint qosHint,
        TraceCausalContext? traceContext)
    {
        var senderProcess = Processes.Resolve(sender);
        if (!senderProcess.IsSuccess)
            return KernelResult<ChannelEnvelope>.Fail(senderProcess.Error, senderProcess.Message!);
        var receiverProcess = Processes.Resolve(receiver);
        if (!receiverProcess.IsSuccess)
            return KernelResult<ChannelEnvelope>.Fail(receiverProcess.Error, receiverProcess.Message!);

        lock (_requestResponseCorrelationGate)
        {
            var responsePreflight = Responses.CanRegisterRequest(endpoint, messageId, sender, receiver);
            if (!responsePreflight.IsSuccess)
                return KernelResult<ChannelEnvelope>.Fail(responsePreflight.Error, responsePreflight.Message!);

            var budget = ReserveAttachedBudget(sender,
                [
                    new(ServiceBudgetDimension.IpcMessages, 1),
                    new(ServiceBudgetDimension.IpcBytes, EstimateIpcBytes(payload, secondaryPayload)),
                ],
                BudgetReservationLifetime.IpcQueued,
                qosHint);
            if (!budget.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(budget.Error, budget.Message!);

            var send = secondaryPayload is null
                ? Channels.Send(
                    senderProcess.Value!,
                    receiverProcess.Value!,
                    endpoint,
                    messageId,
                    payload,
                    capabilities)
                : Channels.SendOwnershipPair(
                    senderProcess.Value!,
                    receiverProcess.Value!,
                    endpoint,
                    messageId,
                    payload!,
                    secondaryPayload,
                    capabilities);
            if (!send.IsSuccess)
            {
                _ = ReleaseAttachedBudget(sender, budget.Value);
                return send;
            }

            // A response-capable request must not become observable by the receiver
            // until its exact response correlation is registered. Receive uses the
            // same gate, so enqueue + correlation publication are one visibility step.
            Responses.RegisterRequest(endpoint, send.Value!, sender, receiver);
            if (budget.Value is { } reservation)
                _ipcBudgetReservations.Add((endpoint.ChannelId, send.Value!.Sequence), (sender, receiver, reservation));
            RecordTrace(sender, TraceEventKind.IpcSent, traceContext, "ipc",
                $"{endpoint.ChannelId.Value}:{send.Value!.Sequence}", "queued", "admitted");
            return send;
        }
    }

    public KernelResult<ChannelEnvelope> Receive(ProcessHandle receiver, ChannelEndpointHandle endpoint)
    {
        var process = Processes.Resolve(receiver);
        if (!process.IsSuccess)
            return KernelResult<ChannelEnvelope>.Fail(process.Error, process.Message!);

        lock (_requestResponseCorrelationGate)
        {
            var receive = Channels.Receive(process.Value!, endpoint);
            if (!receive.IsSuccess) return receive;

            Responses.MarkDelivered(endpoint, receive.Value!, receiver);
            if (_ipcBudgetReservations.Remove((endpoint.ChannelId, receive.Value!.Sequence), out var charge))
                _ = ReleaseAttachedBudget(charge.Sender, charge.Reservation);
            RecordTrace(receiver, TraceEventKind.IpcReceived, null, "ipc",
                $"{endpoint.ChannelId.Value}:{receive.Value.Sequence}", "received", "delivered");
            return receive;
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
