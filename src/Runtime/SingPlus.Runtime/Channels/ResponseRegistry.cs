using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

internal sealed class ResponseRegistry
{
    private enum PendingState
    {
        Queued = 0,
        Delivered = 1
    }

    private sealed class PendingRecord(
        ulong requestSequence,
        ResponseMessageDescriptorV1 response,
        ProcessHandle requester,
        EndpointId requesterEndpoint,
        ProcessHandle responder,
        EndpointId responderEndpoint)
    {
        public ulong RequestSequence { get; } = requestSequence;
        public ResponseMessageDescriptorV1 Response { get; } = response;
        public ProcessHandle Requester { get; } = requester;
        public EndpointId RequesterEndpoint { get; } = requesterEndpoint;
        public ProcessHandle Responder { get; } = responder;
        public EndpointId ResponderEndpoint { get; } = responderEndpoint;
        public PendingState State { get; set; } = PendingState.Queued;
    }

    private sealed class ChannelRecord(
        ResponseProtocolDefinitionV1 protocol,
        ProcessHandle leftOwner,
        ProcessHandle rightOwner,
        int capacity)
    {
        public ResponseProtocolDefinitionV1 Protocol { get; } = protocol;
        public ProcessHandle LeftOwner { get; } = leftOwner;
        public ProcessHandle RightOwner { get; } = rightOwner;
        public int Capacity { get; } = capacity;
        public Dictionary<ulong, PendingRecord> Pending { get; } = [];
        public Queue<ResponseEnvelope> LeftResponses { get; } = new();
        public Queue<ResponseEnvelope> RightResponses { get; } = new();
    }

    private readonly RegionAuthority _regions;
    private readonly Dictionary<ChannelId, ChannelRecord> _channels = [];

    internal ResponseRegistry(RegionAuthority regions)
    {
        _regions = regions;
    }

    internal static KernelResult ValidateProtocol(
        ProtocolDefinitionV1 requestProtocol,
        ResponseProtocolDefinitionV1 responseProtocol)
    {
        if (!string.Equals(requestProtocol.ContractName, responseProtocol.ContractName, StringComparison.Ordinal))
            return KernelResult.Fail(
                KernelError.ResponseProtocolMismatch,
                "Request and response protocol contract names do not match.");

        var requestIds = requestProtocol.Messages.Select(static message => message.MessageId).OrderBy(static id => id).ToArray();
        var responseIds = responseProtocol.Messages.Select(static message => message.MessageId).OrderBy(static id => id).ToArray();
        if (!requestIds.SequenceEqual(responseIds))
            return KernelResult.Fail(
                KernelError.ResponseProtocolMismatch,
                "Response protocol must define exactly one response shape for every request message.");

        foreach (var request in requestProtocol.Messages)
        {
            if (!responseProtocol.TryGetMessage(request.MessageId, out var response))
                return KernelResult.Fail(KernelError.ResponseProtocolMismatch, $"Response metadata for message {request.MessageId} is missing.");
            if (!string.Equals(request.Name, response.Name, StringComparison.Ordinal))
                return KernelResult.Fail(KernelError.ResponseProtocolMismatch, $"Response metadata name for message {request.MessageId} does not match the request protocol.");
            if (request.ReturnsOwnership != (response.Payload.Kind == ResponsePayloadKind.Ownership))
                return KernelResult.Fail(KernelError.ResponseProtocolMismatch, $"Ownership-return metadata for message {request.MessageId} does not match the response protocol.");
            if (request.ReturnsOwnership && request.ReturnOwnershipPayloadKind != response.Payload.OwnershipPayloadKind)
                return KernelResult.Fail(KernelError.ResponseProtocolMismatch, $"Returned ownership kind for message {request.MessageId} does not match the response protocol.");
        }

        return KernelResult.Ok();
    }

    internal void RegisterChannel(
        ChannelEndpointHandle left,
        ChannelEndpointHandle right,
        ResponseProtocolDefinitionV1 protocol,
        ProcessHandle leftOwner,
        ProcessHandle rightOwner,
        int capacity)
    {
        if (left.ChannelId != right.ChannelId)
            throw new InvalidOperationException("Response channel endpoints must belong to the same channel.");
        _channels.Add(left.ChannelId, new ChannelRecord(protocol, leftOwner, rightOwner, capacity));
    }

    internal KernelResult CanRegisterRequest(
        ChannelEndpointHandle endpoint,
        uint messageId,
        ProcessHandle requester,
        ProcessHandle responder)
    {
        if (!_channels.TryGetValue(endpoint.ChannelId, out var channel)) return KernelResult.Ok();
        if (!channel.Protocol.TryGetMessage(messageId, out _))
            return KernelResult.Fail(KernelError.ResponseProtocolMismatch, $"Response protocol does not define message {messageId}.");

        var expectedRequester = endpoint.EndpointId.Value == 1 ? channel.LeftOwner : channel.RightOwner;
        var expectedResponder = endpoint.EndpointId.Value == 1 ? channel.RightOwner : channel.LeftOwner;
        if (requester != expectedRequester || responder != expectedResponder)
            return KernelResult.Fail(KernelError.WrongEndpointOwner, "Response correlation owners do not match the channel endpoints.");

        var outstanding = channel.Pending.Count + channel.LeftResponses.Count + channel.RightResponses.Count;
        return outstanding >= channel.Capacity
            ? KernelResult.Fail(KernelError.CapacityExhausted, "Response transport capacity is exhausted by outstanding calls or unread responses.")
            : KernelResult.Ok();
    }

    internal void RegisterRequest(
        ChannelEndpointHandle endpoint,
        ChannelEnvelope request,
        ProcessHandle requester,
        ProcessHandle responder)
    {
        if (!_channels.TryGetValue(endpoint.ChannelId, out var channel)) return;
        if (!channel.Protocol.TryGetMessage(request.MessageId, out var response))
            throw new InvalidOperationException("Validated response metadata disappeared before request registration.");

        var requesterEndpoint = endpoint.EndpointId;
        var responderEndpoint = new EndpointId(endpoint.EndpointId.Value == 1 ? 2UL : 1UL);
        channel.Pending.Add(
            request.Sequence,
            new PendingRecord(request.Sequence, response, requester, requesterEndpoint, responder, responderEndpoint));
    }

    internal void MarkDelivered(
        ChannelEndpointHandle endpoint,
        ChannelEnvelope request,
        ProcessHandle receiver)
    {
        if (!_channels.TryGetValue(endpoint.ChannelId, out var channel)) return;
        if (!channel.Pending.TryGetValue(request.Sequence, out var pending))
            throw new InvalidOperationException("Delivered request has no pending response correlation.");
        if (pending.Responder != receiver || pending.ResponderEndpoint != endpoint.EndpointId)
            throw new InvalidOperationException("Delivered request does not match the pending response responder.");
        pending.State = PendingState.Delivered;
    }

    internal KernelResult<ProcessHandle> ResolveRequester(
        ChannelEndpointHandle endpoint,
        ulong requestSequence,
        ProcessHandle responder)
    {
        if (!_channels.TryGetValue(endpoint.ChannelId, out var channel))
            return KernelResult<ProcessHandle>.Fail(KernelError.ResponseProtocolUnavailable, "The channel was not created with response protocol metadata.");
        if (!channel.Pending.TryGetValue(requestSequence, out var pending))
            return KernelResult<ProcessHandle>.Fail(KernelError.ResponseNotPending, "No pending response exists for the request sequence.");
        if (pending.Responder != responder || pending.ResponderEndpoint != endpoint.EndpointId)
            return KernelResult<ProcessHandle>.Fail(KernelError.WrongEndpointOwner, "The endpoint does not own this pending response.");
        return KernelResult<ProcessHandle>.Ok(pending.Requester);
    }

    internal KernelResult<ResponseEnvelope> Publish(
        SingProcess responder,
        SingProcess requester,
        ChannelEndpointHandle endpoint,
        ulong requestSequence,
        object? payload)
    {
        if (!_channels.TryGetValue(endpoint.ChannelId, out var channel))
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseProtocolUnavailable, "The channel was not created with response protocol metadata.");
        if (!channel.Pending.TryGetValue(requestSequence, out var pending))
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotPending, "No pending response exists for the request sequence.");

        var responderHandle = new ProcessHandle(responder.ProcessId, responder.Generation);
        var requesterHandle = new ProcessHandle(requester.ProcessId, requester.Generation);
        if (pending.Responder != responderHandle || pending.ResponderEndpoint != endpoint.EndpointId)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.WrongEndpointOwner, "The endpoint does not own this pending response.");
        if (pending.Requester != requesterHandle)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.WrongEndpointOwner, "The original requester generation no longer matches the pending response.");
        if (pending.State != PendingState.Delivered)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotDelivered, "A response cannot be published before the request is delivered to the responder.");

        var shape = ValidatePayload(pending.Response.Payload, payload);
        if (!shape.IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(shape.Error, shape.Message!);

        object? publishedPayload = payload;
        if (pending.Response.Payload.Kind == ResponsePayloadKind.Ownership)
        {
            var owned = (ITransferableOwnedPayload)payload!;
            if (!owned.IsValidForRuntime)
                return KernelResult<ResponseEnvelope>.Fail(KernelError.InvalidRegionState, "Ownership response requires a valid owned payload.");

            var oldHandle = owned.Handle;
            var source = new RegionOwner(responder.DomainId, responder.Generation);
            var target = new RegionOwner(requester.DomainId, requester.Generation);
            var regionValidation = _regions.Validate(oldHandle, source);
            if (!regionValidation.IsSuccess)
                return KernelResult<ResponseEnvelope>.Fail(regionValidation.Error, regionValidation.Message!);

            var transfer = _regions.Transfer(oldHandle, source, target);
            if (!transfer.IsSuccess)
                return KernelResult<ResponseEnvelope>.Fail(transfer.Error, transfer.Message!);

            publishedPayload = owned.TransferForRuntime(transfer.Value);
            _regions.ReplacePayload(oldHandle, transfer.Value, (ITransferableOwnedPayload)publishedPayload);
            responder.RemoveRegion(oldHandle);
            requester.AddRegion(transfer.Value);
        }

        var envelope = new ResponseEnvelope(
            requestSequence,
            pending.Response.MessageId,
            ResponsePublicationStatus.Published,
            publishedPayload);
        ResponseQueue(channel, pending.RequesterEndpoint).Enqueue(envelope);
        channel.Pending.Remove(requestSequence);
        return KernelResult<ResponseEnvelope>.Ok(envelope);
    }

    internal KernelResult<ResponseEnvelope> Cancel(
        ProcessHandle responder,
        ChannelEndpointHandle endpoint,
        ulong requestSequence)
    {
        if (!_channels.TryGetValue(endpoint.ChannelId, out var channel))
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseProtocolUnavailable, "The channel was not created with response protocol metadata.");
        if (!channel.Pending.TryGetValue(requestSequence, out var pending))
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotPending, "No pending response exists for the request sequence.");
        if (pending.Responder != responder || pending.ResponderEndpoint != endpoint.EndpointId)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.WrongEndpointOwner, "The endpoint does not own this pending response.");
        if (pending.State != PendingState.Delivered)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotDelivered, "A response cannot be cancelled before the request is delivered to the responder.");

        var envelope = new ResponseEnvelope(
            requestSequence,
            pending.Response.MessageId,
            ResponsePublicationStatus.Cancelled,
            null);
        ResponseQueue(channel, pending.RequesterEndpoint).Enqueue(envelope);
        channel.Pending.Remove(requestSequence);
        return KernelResult<ResponseEnvelope>.Ok(envelope);
    }

    internal KernelResult<ResponseEnvelope> Receive(
        ProcessHandle requester,
        ChannelEndpointHandle endpoint)
    {
        if (!_channels.TryGetValue(endpoint.ChannelId, out var channel))
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseProtocolUnavailable, "The channel was not created with response protocol metadata.");

        var expectedOwner = endpoint.EndpointId.Value == 1 ? channel.LeftOwner : channel.RightOwner;
        if (expectedOwner != requester)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.WrongEndpointOwner, "Endpoint is not owned by the response receiver.");

        var queue = ResponseQueue(channel, endpoint.EndpointId);
        return queue.Count == 0
            ? KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotAvailable, "No published or cancelled response is available.")
            : KernelResult<ResponseEnvelope>.Ok(queue.Dequeue());
    }

    private static Queue<ResponseEnvelope> ResponseQueue(ChannelRecord channel, EndpointId endpoint) =>
        endpoint.Value == 1 ? channel.LeftResponses : channel.RightResponses;

    private static KernelResult ValidatePayload(ResponsePayloadDescriptorV1 expected, object? payload)
    {
        switch (expected.Kind)
        {
            case ResponsePayloadKind.None:
                return payload is null
                    ? KernelResult.Ok()
                    : KernelResult.Fail(KernelError.UnsupportedPayload, "Response does not declare a payload.");

            case ResponsePayloadKind.Primitive:
                if (payload is null) return KernelResult.Fail(KernelError.UnsupportedPayload, "Response requires a primitive payload.");
                var primitiveTypeName = payload.GetType().FullName ?? payload.GetType().Name;
                return string.Equals(primitiveTypeName, expected.TypeName, StringComparison.Ordinal)
                    ? KernelResult.Ok()
                    : KernelResult.Fail(KernelError.UnsupportedPayload, $"Expected primitive response type {expected.TypeName}, got {primitiveTypeName}.");

            case ResponsePayloadKind.Enum:
                if (payload is not Enum) return KernelResult.Fail(KernelError.UnsupportedPayload, "Response requires the declared enum payload.");
                var enumTypeName = payload.GetType().FullName ?? payload.GetType().Name;
                return string.Equals(enumTypeName, expected.TypeName, StringComparison.Ordinal)
                    ? KernelResult.Ok()
                    : KernelResult.Fail(KernelError.UnsupportedPayload, $"Expected enum response type {expected.TypeName}, got {enumTypeName}.");

            case ResponsePayloadKind.Bounded:
                if (payload is not IBoundedPayload bounded)
                    return KernelResult.Fail(KernelError.UnsupportedPayload, "Response requires the declared bounded payload.");
                var boundedTypeName = payload.GetType().FullName ?? payload.GetType().Name;
                if (!string.Equals(boundedTypeName, expected.TypeName, StringComparison.Ordinal))
                    return KernelResult.Fail(KernelError.UnsupportedPayload, $"Expected bounded response type {expected.TypeName}, got {boundedTypeName}.");
                if (bounded.PayloadSize < 0)
                    return KernelResult.Fail(KernelError.UnsupportedPayload, "Bounded response payload size cannot be negative.");
                if (bounded.MaxPayloadSize != expected.MaxBytes)
                    return KernelResult.Fail(KernelError.UnsupportedPayload, $"Bounded response self-reported limit {bounded.MaxPayloadSize} does not match contract MaxBytes {expected.MaxBytes}.");
                if (bounded.PayloadSize > expected.MaxBytes)
                    return KernelResult.Fail(KernelError.UnsupportedPayload, $"Bounded response size {bounded.PayloadSize} exceeds contract MaxBytes {expected.MaxBytes}.");
                return KernelResult.Ok();

            case ResponsePayloadKind.Ownership:
                if (payload is not ITransferableOwnedPayload owned)
                    return KernelResult.Fail(KernelError.UnsupportedPayload, "Response requires the declared ownership payload.");
                return owned.PayloadKind == expected.OwnershipPayloadKind
                    ? KernelResult.Ok()
                    : KernelResult.Fail(KernelError.UnsupportedPayload, $"Expected ownership response kind {expected.OwnershipPayloadKind}, got {owned.PayloadKind}.");

            default:
                return KernelResult.Fail(KernelError.UnsupportedPayload, "Response payload kind is not supported.");
        }
    }
}
