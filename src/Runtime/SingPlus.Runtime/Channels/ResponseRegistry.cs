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

    private sealed class CompletionRecord(
        ProcessHandle requester,
        EndpointId requesterEndpoint,
        PendingRecord originalPending)
    {
        public ProcessHandle Requester { get; } = requester;
        public EndpointId RequesterEndpoint { get; } = requesterEndpoint;
        public PendingRecord OriginalPending { get; } = originalPending;
        public TaskCompletionSource<ResponseEnvelope> Source { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Waiting { get; set; }
        public ResponseEnvelope? Consumed; // Published once by this delivery owner; safe pure lookup after route retirement.
        public ResponseEnvelope? RawPublication { get; set; }
        public CompletionRecord? OriginalSelf { get; set; }
        public bool PublicationCommitted { get; set; }
        public object? OriginalOwnershipInput { get; set; }
        public object? OwnershipProjection { get; set; }
        public bool OwnershipTransferAttempted { get; set; }
        public PreparedOwnedTransfer? PreparedOwnership { get; set; }
        public TransferProducer? OriginalTransferProducer { get; set; }
        public InitialOwnership? OriginalInitialOwnership { get; set; }
        public bool FailedTransferCleanupCompleted { get; set; }
        public object? OriginalFailedTransferCompletion { get; set; }
        public object? FailedTransferCompletion { get; set; }
        public ResponseEnvelope? OriginalChannelCancellation { get; set; }
        public object? OriginalPublishedRegionOwner { get; set; }
        public RegionHandle? OriginalPublishedRegion { get; set; }
        public object? OriginalChannelIdentity { get; init; }
    }

    private sealed class ChannelRecord(
        ResponseProtocolDefinitionV1 protocol,
        ProcessHandle leftOwner,
        ProcessHandle rightOwner,
        int capacity)
    {
        public ResponseRegistry Owner { get; init; } = null!;
        public ChannelRecord OriginalSelf { get; set; } = null!;
        public ChannelId OriginalChannel { get; init; }
        public object Identity { get; } = new(); // No backing/ChannelRecord reference in completion history.
        public ulong OriginalGeneration { get; init; }
        public bool Closed { get; set; }
        public ResponseProtocolDefinitionV1 Protocol { get; } = protocol;
        public ProcessHandle LeftOwner { get; } = leftOwner;
        public ProcessHandle RightOwner { get; } = rightOwner;
        public int Capacity { get; } = capacity;
        public Dictionary<ulong, PendingRecord> Pending { get; } = [];
        public Dictionary<ulong, CompletionRecord> Completions { get; } = [];
        public Dictionary<ulong, CompletionRecord> OwnedCompletions { get; } = [];
        public Queue<ResponseEnvelope> LeftResponses { get; } = new();
        public Queue<ResponseEnvelope> RightResponses { get; } = new();
    }

    private readonly RegionAuthority _regions;
    private readonly Func<SingProcess, SingProcess, RegionHandle, Func<KernelResult>?, Action, PreparedOwnedTransfer, KernelResult<RegionHandle>> _transferRegion;
    private readonly ChannelRegistry _requestChannels;
    private readonly Dictionary<ChannelId, ChannelRecord> _channels = [];
    // Same authoritative records retained independently from the response routing index.
    private readonly Dictionary<ChannelId, ChannelRecord> _ownedChannels = [];

    internal ResponseRegistry(RegionAuthority regions, ChannelRegistry channels,
        Func<SingProcess, SingProcess, RegionHandle, Func<KernelResult>?, Action, PreparedOwnedTransfer, KernelResult<RegionHandle>> transferRegion)
    {
        _regions = regions;
        _transferRegion = transferRegion;
        _requestChannels = channels;
        channels.ChannelClosed += CloseChannel;
    }

    internal static KernelResult ValidateProtocol(
        ProtocolDefinitionV1 requestProtocol,
        ResponseProtocolDefinitionV1 responseProtocol)
    {
        if (!string.Equals(requestProtocol.ContractName, responseProtocol.ContractName, StringComparison.Ordinal))
            return KernelResult.Fail(
                KernelError.ResponseProtocolMismatch,
                "Request and response protocol contract names do not match.");

        uint[] requestIds = requestProtocol.Messages.Select(static message => message.MessageId).OrderBy(static id => id).ToArray();
        uint[] responseIds = responseProtocol.Messages.Select(static message => message.MessageId).OrderBy(static id => id).ToArray();
        if (!requestIds.SequenceEqual(responseIds))
            return KernelResult.Fail(
                KernelError.ResponseProtocolMismatch,
                "Response protocol must define exactly one response shape for every request message.");

        foreach (ProtocolMessageDescriptorV1 request in requestProtocol.Messages)
        {
            if (!responseProtocol.TryGetMessage(request.MessageId, out ResponseMessageDescriptorV1? response))
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
        _ownedChannels.EnsureCapacity(_ownedChannels.Count + 1);
        _channels.EnsureCapacity(_channels.Count + 1);
        ChannelRecord record = new ChannelRecord(protocol, leftOwner, rightOwner, capacity){Owner=this,OriginalChannel=left.ChannelId,OriginalGeneration=left.Generation};
        record.OriginalSelf=record;
        _ownedChannels.Add(left.ChannelId, record);
        _channels.Add(left.ChannelId, record);
    }

    internal KernelResult CanRegisterRequest(
        ChannelEndpointHandle endpoint,
        uint messageId,
        ProcessHandle requester,
        ProcessHandle responder)
    {
        if (!TryResolveChannel(endpoint.ChannelId, out ChannelRecord? channel))
            return _ownedChannels.ContainsKey(endpoint.ChannelId) || _channels.ContainsKey(endpoint.ChannelId)
                ? KernelResult.Fail(KernelError.PlatformFaulted, "Original response owner continuity was lost.")
                : KernelResult.Ok();
        if (!channel.Protocol.TryGetMessage(messageId, out _))
            return KernelResult.Fail(KernelError.ResponseProtocolMismatch, $"Response protocol does not define message {messageId}.");

        ProcessHandle expectedRequester = endpoint.EndpointId.Value == 1 ? channel.LeftOwner : channel.RightOwner;
        ProcessHandle expectedResponder = endpoint.EndpointId.Value == 1 ? channel.RightOwner : channel.LeftOwner;
        if (requester != expectedRequester || responder != expectedResponder)
            return KernelResult.Fail(KernelError.WrongEndpointOwner, "Response correlation owners do not match the channel endpoints.");

        int outstanding = channel.OwnedCompletions.Count;
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
        if (!TryResolveChannel(endpoint.ChannelId, out ChannelRecord? channel)) return;
        if (!channel.Protocol.TryGetMessage(request.MessageId, out ResponseMessageDescriptorV1? response))
            throw new InvalidOperationException("Validated response metadata disappeared before request registration.");

        EndpointId requesterEndpoint = endpoint.EndpointId;
        EndpointId responderEndpoint = new EndpointId(endpoint.EndpointId.Value == 1 ? 2UL : 1UL);
        channel.OwnedCompletions.EnsureCapacity(channel.OwnedCompletions.Count + 1);
        channel.Pending.EnsureCapacity(channel.Pending.Count + 1);
        channel.Completions.EnsureCapacity(channel.Completions.Count + 1);
        PendingRecord pending = new PendingRecord(request.Sequence, response, requester, requesterEndpoint, responder, responderEndpoint);
        CompletionRecord completion = new CompletionRecord(requester, requesterEndpoint, pending){OriginalChannelIdentity=channel.Identity};
        completion.OriginalSelf=completion;
        channel.OwnedCompletions.Add(request.Sequence, completion);
        channel.Pending.Add(request.Sequence, pending);
        channel.Completions.Add(request.Sequence, completion);
    }

    internal KernelResult CanMarkDelivered(ChannelEndpointHandle endpoint, ChannelEnvelope request, ProcessHandle receiver)
    {
        if (!TryResolveChannel(endpoint.ChannelId, out ChannelRecord? channel))
            return _ownedChannels.ContainsKey(endpoint.ChannelId) || _channels.ContainsKey(endpoint.ChannelId)
                ? KernelResult.Fail(KernelError.PlatformFaulted, "Original response owner continuity was lost.")
                : KernelResult.Ok();
        if (!TryResolvePending(channel, request.Sequence, out PendingRecord? pending) ||
            pending.Response.MessageId != request.MessageId || pending.State != PendingState.Queued)
            return KernelResult.Fail(KernelError.ResponseNotPending, "Original request correlation is not available for delivery.");
        return pending.Responder == receiver && pending.ResponderEndpoint == endpoint.EndpointId
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.WrongEndpointOwner, "Delivered request does not match its original responder.");
    }

    internal void MarkDelivered(
        ChannelEndpointHandle endpoint,
        ChannelEnvelope request,
        ProcessHandle receiver)
    {
        if (!TryResolveChannel(endpoint.ChannelId, out ChannelRecord? channel)) return;
        if (!TryResolvePending(channel, request.Sequence, out PendingRecord? pending))
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
        if (!TryResolveChannel(endpoint.ChannelId, out ChannelRecord? channel))
            return KernelResult<ProcessHandle>.Fail(KernelError.ResponseProtocolUnavailable, "The channel was not created with response protocol metadata.");
        if (!TryResolvePending(channel, requestSequence, out PendingRecord? pending))
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
        object? payload,
        Func<SingProcess, SingProcess, RegionHandle, Func<KernelResult>?, Action, PreparedOwnedTransfer, KernelResult<RegionHandle>>? transferRegion = null)
    {
        if (!TryResolveChannel(endpoint.ChannelId, out ChannelRecord? channel))
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseProtocolUnavailable, "The channel was not created with response protocol metadata.");
        if (!TryResolvePending(channel, requestSequence, out PendingRecord? pending))
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotPending, "No pending response exists for the request sequence.");

        ProcessHandle responderHandle = new ProcessHandle(responder.ProcessId, responder.Generation);
        ProcessHandle requesterHandle = new ProcessHandle(requester.ProcessId, requester.Generation);
        if (pending.Responder != responderHandle || pending.ResponderEndpoint != endpoint.EndpointId)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.WrongEndpointOwner, "The endpoint does not own this pending response.");
        if (pending.Requester != requesterHandle)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.WrongEndpointOwner, "The original requester generation no longer matches the pending response.");
        if (pending.State != PendingState.Delivered)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotDelivered, "A response cannot be published before the request is delivered to the responder.");
        if(!TryResolveCompletion(channel,requestSequence,out CompletionRecord? originalCompletion) ||
            originalCompletion.Source.Task.IsCompleted || originalCompletion.RawPublication is not null)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotPending,"Original completion already finalized; ownership transfer/publication denied.");

        KernelResult shape = ValidatePayload(pending.Response.Payload, payload);
        if (!shape.IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(shape.Error, shape.Message!);

        object? publishedPayload = payload;
        if (pending.Response.Payload.Kind == ResponsePayloadKind.Ownership)
        {
            if(originalCompletion.OwnershipTransferAttempted)
                return KernelResult<ResponseEnvelope>.Fail(KernelError.PlatformFaulted,"Original ownership response transfer already attempted; no repeat.");
            originalCompletion.OriginalOwnershipInput=payload;
            originalCompletion.OwnershipTransferAttempted=true; // Existing completion owner before possible transfer.
            KernelResult<object> transferred = TransferOwnership(responder, requester, (ITransferableOwnedPayload)payload!, transferRegion: transferRegion,completionOwner:originalCompletion);
            if (!transferred.IsSuccess)
                return KernelResult<ResponseEnvelope>.Fail(transferred.Error, transferred.Message!);
            publishedPayload = transferred.Value;
            originalCompletion.OwnershipProjection=publishedPayload; // Original returned projection before envelope/publication.
        }

        ResponseEnvelope envelope = new ResponseEnvelope(
            requestSequence,
            pending.Response.MessageId,
            ResponsePublicationStatus.Published,
            publishedPayload);
        Complete(channel, pending, envelope);
        return KernelResult<ResponseEnvelope>.Ok(envelope);
    }

    // The same authoritative ownership projection used by ordinary response
    // publication, without allocating a response envelope. Invocation settlement
    // remains the responsibility of its existing owner.
    internal KernelResult<OwnedBuffer<T>> TransferInlineOwnership<T>(
        SingProcess responder,
        SingProcess requester,
        ChannelEndpointHandle endpoint,
        uint messageId,
        OwnedBuffer<T> payload,
        Func<KernelResult>? finalAdmission = null,
        Func<SingProcess, SingProcess, RegionHandle, Func<KernelResult>?, Action, PreparedOwnedTransfer, KernelResult<RegionHandle>>? transferRegion = null) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!TryResolveChannel(endpoint.ChannelId, out ChannelRecord? channel))
            return KernelResult<OwnedBuffer<T>>.Fail(KernelError.ResponseProtocolUnavailable, "Inline ownership response requires registered response metadata.");
        if (!channel.Protocol.TryGetMessage(messageId, out ResponseMessageDescriptorV1? response))
            return KernelResult<OwnedBuffer<T>>.Fail(KernelError.ResponseProtocolMismatch, $"Response metadata for message {messageId} is missing.");
        ProcessHandle responderHandle = new ProcessHandle(responder.ProcessId, responder.Generation);
        ProcessHandle requesterHandle = new ProcessHandle(requester.ProcessId, requester.Generation);
        ProcessHandle expectedResponder = endpoint.EndpointId.Value == 1 ? channel.LeftOwner : channel.RightOwner;
        ProcessHandle expectedRequester = endpoint.EndpointId.Value == 1 ? channel.RightOwner : channel.LeftOwner;
        if (responderHandle != expectedResponder || requesterHandle != expectedRequester)
            return KernelResult<OwnedBuffer<T>>.Fail(KernelError.WrongEndpointOwner, "Inline ownership response parties do not match the channel.");
        KernelResult shape = ValidatePayload(response.Payload, payload);
        if (!shape.IsSuccess)
            return KernelResult<OwnedBuffer<T>>.Fail(shape.Error, shape.Message!);
        if (response.Payload.Kind != ResponsePayloadKind.Ownership)
            return KernelResult<OwnedBuffer<T>>.Fail(KernelError.UnsupportedPayload, "Operation does not declare an ownership response.");
        KernelResult<object> transferred = TransferOwnership(responder, requester, payload, finalAdmission, transferRegion);
        return transferred.IsSuccess
            ? KernelResult<OwnedBuffer<T>>.Ok((OwnedBuffer<T>)transferred.Value!)
            : KernelResult<OwnedBuffer<T>>.Fail(transferred.Error, transferred.Message!);
    }

    private KernelResult<object> TransferOwnership(
        SingProcess responder,
        SingProcess requester,
        ITransferableOwnedPayload owned,
        Func<KernelResult>? finalAdmission = null,
        Func<SingProcess, SingProcess, RegionHandle, Func<KernelResult>?, Action, PreparedOwnedTransfer, KernelResult<RegionHandle>>? transferRegion = null,
        CompletionRecord? completionOwner = null)
    {
        KernelResult<object> RefuseInitialPayload(KernelError error,string message)
        {
            if(completionOwner is { OriginalInitialOwnership: { } initial } &&
                ReferenceEquals(completionOwner.OriginalSelf,completionOwner) && ReferenceEquals(initial.Original,initial) &&
                ReferenceEquals(initial.Payload,owned) && ReferenceEquals(completionOwner.OriginalOwnershipInput,owned) &&
                completionOwner.PreparedOwnership is null && completionOwner.OriginalTransferProducer is null && initial.Refusal is null)
                initial.Refusal=error; // Own checked refusal before preparation/Region invocation.
            return KernelResult<object>.Fail(error,message);
        }
        if (!owned.IsValidForRuntime)
            return RefuseInitialPayload(KernelError.InvalidRegionState,"Ownership response requires a valid owned payload.");
        RegionHandle oldHandle = owned.Handle;
        RegionOwner source = new RegionOwner(responder.DomainId, responder.Generation);
        RegionOwner target = new RegionOwner(requester.DomainId, requester.Generation);
        KernelResult<RegionDescriptor> regionValidation = _regions.Validate(oldHandle, source);
        if (!regionValidation.IsSuccess)
            return RefuseInitialPayload(regionValidation.Error,regionValidation.Message!);
        KernelResult<ITransferableOwnedPayload> captured =_regions.CaptureTransferPayload(oldHandle,source);
        if(!captured.IsSuccess || !ReferenceEquals(captured.Value,owned))
            return RefuseInitialPayload(KernelError.StaleHandle,"Ownership response caller is not the original registered Region payload.");
        PreparedOwnedTransfer prepared;
        try { prepared=owned.PrepareTransferForRuntime(); }
        catch(InvalidOperationException exception){return RefuseInitialPayload(KernelError.RegionUseConflict,exception.Message);}
        if(completionOwner is not null)
        {
            if(!ReferenceEquals(completionOwner.OriginalSelf,completionOwner) || !ReferenceEquals(completionOwner.OriginalOwnershipInput,owned) ||
                completionOwner.PreparedOwnership is not null)
            {
                prepared.Dispose();
                return KernelResult<object>.Fail(KernelError.PlatformFaulted,"Original prepared ownership owner changed; no transfer.");
            }
            completionOwner.PreparedOwnership=prepared;
            completionOwner.OwnershipProjection=prepared.Projection; // Invalid until exact token commit, not ownership proof.
        }
        bool refusedBeforeEffect =false;
        KernelResult AdmitOriginal()
        {
            KernelResult admitted =finalAdmission?.Invoke()??KernelResult.Ok();
            if(!admitted.IsSuccess){refusedBeforeEffect=true;return admitted;}
            KernelResult<ITransferableOwnedPayload> current =_regions.CaptureTransferPayload(oldHandle,source);
            if(!current.IsSuccess || !ReferenceEquals(current.Value,owned))
            {
                refusedBeforeEffect=true;
                return KernelResult.Fail(KernelError.StaleHandle,"Original response payload changed during transfer preparation.");
            }
            return KernelResult.Ok();
        }
        KernelResult<RegionHandle> transfer = (transferRegion ?? _transferRegion)(responder, requester, oldHandle, AdmitOriginal, () => refusedBeforeEffect=true,prepared);
        if (!transfer.IsSuccess)
        {
            if(refusedBeforeEffect)prepared.Dispose(); // Exact trusted admission refusal, never inferred from generic failure.
            return KernelResult<object>.Fail(transfer.Error, transfer.Message!);
        }
        // Region owner commits prepared custody/payload atomically with transfer.
        object moved = prepared.Projection;

        responder.RemoveRegion(oldHandle);
        requester.AddRegion(transfer.Value);
        return KernelResult<object>.Ok(moved);
    }

    internal KernelResult<ResponseEnvelope> Cancel(
        ProcessHandle responder,
        ChannelEndpointHandle endpoint,
        ulong requestSequence)
    {
        if (!TryResolveChannel(endpoint.ChannelId, out ChannelRecord? channel))
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseProtocolUnavailable, "The channel was not created with response protocol metadata.");
        if (!TryResolvePending(channel, requestSequence, out PendingRecord? pending))
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotPending, "No pending response exists for the request sequence.");
        if (pending.Responder != responder || pending.ResponderEndpoint != endpoint.EndpointId)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.WrongEndpointOwner, "The endpoint does not own this pending response.");
        if (pending.State != PendingState.Delivered)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotDelivered, "A response cannot be cancelled before the request is delivered to the responder.");
        KernelResult transition = _requestChannels.ApplyCancellationTransition(endpoint, pending.Response.MessageId);
        if (!transition.IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(transition.Error, transition.Message!);

        ResponseEnvelope envelope = new ResponseEnvelope(
            requestSequence,
            pending.Response.MessageId,
            ResponsePublicationStatus.Cancelled,
            null);
        Complete(channel, pending, envelope);
        return KernelResult<ResponseEnvelope>.Ok(envelope);
    }

    internal KernelResult<ResponseEnvelope> Receive(
        ProcessHandle requester,
        ChannelEndpointHandle endpoint, bool closedCleanup = false)
    {
        ChannelRecord channel=null!;
        bool resolved =closedCleanup
            ? TryResolveClosedChannel(endpoint,out channel)
            : TryResolveChannel(endpoint.ChannelId,out channel!);
        if (!resolved)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseProtocolUnavailable, "The channel was not created with response protocol metadata.");

        ProcessHandle expectedOwner = endpoint.EndpointId.Value == 1 ? channel.LeftOwner : channel.RightOwner;
        if (expectedOwner != requester)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.WrongEndpointOwner, "Endpoint is not owned by the response receiver.");

        Queue<ResponseEnvelope> queue = ResponseQueue(channel, endpoint.EndpointId);
        if (queue.Count == 0)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotAvailable, "No published or cancelled response is available.");

        ResponseEnvelope next = queue.Peek();
        if (!TryResolveCompletion(channel, next.RequestSequence, out CompletionRecord? completion) ||
            !completion.Source.Task.IsCompletedSuccessfully || !ReferenceEquals(completion.Source.Task.Result, next) ||
            !HasOriginalPublishedPayload(completion,next))
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotAvailable, "Original response completion continuity was lost.");
        if (completion.Waiting)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotAvailable, "The next response is reserved by a correlated runtime waiter.");

        ResponseEnvelope envelope = queue.Dequeue();
        Volatile.Write(ref completion.Consumed,envelope);
        channel.Completions.Remove(envelope.RequestSequence);
        channel.OwnedCompletions.Remove(envelope.RequestSequence);
        if(channel.Closed && channel.OwnedCompletions.Count==0)_ownedChannels.Remove(endpoint.ChannelId);
        return KernelResult<ResponseEnvelope>.Ok(envelope);
    }

    internal async ValueTask<KernelResult<ResponseEnvelope>> WaitAsync(
        ProcessHandle requester,
        ChannelEndpointHandle endpoint,
        ulong requestSequence,
        object correlationGate, bool closedCleanup = false)
    {
        ChannelRecord channel=null!;
        if (!(closedCleanup ? TryResolveClosedChannel(endpoint,out channel) : TryResolveChannel(endpoint.ChannelId,out channel)))
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseProtocolUnavailable, "The channel was not created with response protocol metadata.");

        ProcessHandle expectedOwner = endpoint.EndpointId.Value == 1 ? channel.LeftOwner : channel.RightOwner;
        if (expectedOwner != requester)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.WrongEndpointOwner, "Endpoint is not owned by the response waiter.");
        if (!TryResolveCompletion(channel, requestSequence, out CompletionRecord? completion))
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotAvailable, "The correlated response is no longer pending or available.");
        if (completion.Requester != requester || completion.RequesterEndpoint != endpoint.EndpointId)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.WrongEndpointOwner, "The correlated response does not belong to this endpoint.");
        if (completion.Waiting)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotAvailable, "A runtime waiter is already registered for this response.");
        if(completion.Source.Task.IsCompletedSuccessfully && !HasOriginalPublishedPayload(completion,completion.Source.Task.Result))
            return KernelResult<ResponseEnvelope>.Fail(KernelError.PlatformFaulted,"Original ownership response projection continuity was lost.");
        if (closedCleanup && (!completion.PublicationCommitted || completion.Consumed is not null ||
            completion.RawPublication is not { Status:ResponsePublicationStatus.Published,Payload:ITransferableOwnedPayload } raw ||
            !completion.Source.Task.IsCompletedSuccessfully || !ReferenceEquals(completion.Source.Task.Result,raw) ||
            !ResponseQueue(channel,endpoint.EndpointId).Any(queued=>ReferenceEquals(queued,raw))))
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotAvailable,"Exact original closed ownership response is unavailable.");

        completion.Waiting = true;
        ResponseEnvelope envelope = await completion.Source.Task.ConfigureAwait(false);

        // Registration and terminal publication use this existing owner interlock.
        // The continuation must consume the exact record under the same gate.
        lock (correlationGate)
        {
            // Delivery belongs to the captured completion, independently of the
            // channel routing that Close may already have legitimately retired.
            if (!OwnsOriginalChannel(endpoint.ChannelId,channel) ||
                !ReferenceEquals(completion.OriginalSelf,completion) ||
                !ReferenceEquals(completion.OriginalChannelIdentity,channel.Identity) ||
                !completion.Source.Task.IsCompletedSuccessfully || !ReferenceEquals(completion.Source.Task.Result,envelope) ||
                !ReferenceEquals(completion.RawPublication,envelope) && !ReferenceEquals(completion.OriginalChannelCancellation,envelope))
                return KernelResult<ResponseEnvelope>.Fail(KernelError.PlatformFaulted,"Original response delivery continuity was lost.");
            if(!HasOriginalPublishedPayload(completion,envelope))
            {
                // The waiter has ended without consumption. Keep the same raw
                // and completion available for exact restoration/reconciliation.
                if(_ownedChannels.TryGetValue(endpoint.ChannelId,out ChannelRecord? retained) && ReferenceEquals(retained,channel) &&
                    channel.OwnedCompletions.TryGetValue(requestSequence,out CompletionRecord? retainedCompletion) && ReferenceEquals(retainedCompletion,completion))
                {
                    completion.Waiting=false;
                    Queue<ResponseEnvelope> queue =ResponseQueue(channel,endpoint.EndpointId);
                    if(!queue.Any(queued=>ReferenceEquals(queued,envelope)))queue.Enqueue(envelope);
                }
                return KernelResult<ResponseEnvelope>.Fail(KernelError.PlatformFaulted,"Original ownership response projection continuity was lost.");
            }
            Volatile.Write(ref completion.Consumed,envelope);
            if (_ownedChannels.TryGetValue(endpoint.ChannelId, out ChannelRecord? activeChannel) &&
                ReferenceEquals(activeChannel, channel) &&
                channel.OwnedCompletions.TryGetValue(requestSequence, out CompletionRecord? activeCompletion) &&
                ReferenceEquals(activeCompletion, completion))
            {
                RemoveQueuedResponse(ResponseQueue(channel, endpoint.EndpointId), requestSequence);
                channel.Completions.Remove(requestSequence);
                channel.OwnedCompletions.Remove(requestSequence);
                if(channel.Closed && channel.OwnedCompletions.Count==0)_ownedChannels.Remove(endpoint.ChannelId);
            }
        }

        return KernelResult<ResponseEnvelope>.Ok(envelope);
    }

    private void Complete(
        ChannelRecord channel,
        PendingRecord pending,
        ResponseEnvelope envelope)
    {
        if (!TryResolvePending(channel, pending.RequestSequence, out PendingRecord? originalPending) ||
            !ReferenceEquals(originalPending, pending) ||
            !TryResolveCompletion(channel, pending.RequestSequence, out CompletionRecord? completion))
            throw new InvalidOperationException("Pending response completion state disappeared before publication.");
        if(completion.Source.Task.IsCompleted || completion.RawPublication is not null)
            throw new InvalidOperationException("Original response completion was already finalized before publication.");
        completion.RawPublication=envelope; // Existing owner retains raw before observable queue/notification.
        if(envelope.Payload is ITransferableOwnedPayload owned)
        {
            completion.OriginalPublishedRegion=owned.Handle;
            completion.OriginalPublishedRegionOwner=_regions.CaptureOriginalAccountingOwner(owned.Handle);
        }

        if (!completion.Waiting)
            ResponseQueue(channel, pending.RequesterEndpoint).Enqueue(envelope);

        channel.Pending.Remove(pending.RequestSequence);
        if (!completion.Source.TrySetResult(envelope))
            throw new InvalidOperationException("Pending response completion was already finalized.");
        completion.PublicationCommitted=true;
    }

    internal sealed class OriginalPublication
    {
        internal OriginalPublication(ResponseRegistry owner,object completion,ProcessHandle responder,ChannelEndpointHandle endpoint,ulong sequence)
        { Owner=owner;Completion=completion;Responder=responder;Endpoint=endpoint;Sequence=sequence;Original=this; }
        internal readonly ResponseRegistry Owner;
        internal readonly object Completion;
        internal readonly ProcessHandle Responder;
        internal readonly ChannelEndpointHandle Endpoint;
        internal readonly ulong Sequence;
        internal readonly OriginalPublication Original;
    }

    // Original producer correlation in the existing response completion owner.
    internal sealed class InitialOwnership
    {
        internal readonly InitialOwnership Original;
        internal readonly OriginalPublication Publication;
        internal readonly ITransferableOwnedPayload Payload;
        internal readonly RegionHandle Region;
        internal readonly RegionOwner Owner;
        internal readonly object RegionOwner;
        internal readonly (object Fact,object? RegionOwner,ProcessHandle Owner,BudgetReservationHandle Reservation)[] Charges;
        internal KernelError? Refusal;
        internal InitialOwnership(OriginalPublication publication,ITransferableOwnedPayload payload,RegionHandle region,
            RegionOwner owner,object regionOwner,(object Fact,object? RegionOwner,ProcessHandle Owner,BudgetReservationHandle Reservation)[] charges)
        { Original=this;Publication=publication;Payload=payload;Region=region;Owner=owner;RegionOwner=regionOwner;Charges=charges.ToArray(); }
    }

    internal KernelResult RetainInitialOwnership(InitialOwnership fact)
    {
        OriginalPublication original =fact.Publication;
        if(!ReferenceEquals(fact.Original,fact) || !ReferenceEquals(original.Original,original) || !ReferenceEquals(original.Owner,this) ||
            original.Completion is not CompletionRecord completion || !ReferenceEquals(completion.OriginalSelf,completion) ||
            completion.OriginalTransferProducer is not null || completion.OwnershipTransferAttempted || completion.RawPublication is not null ||
            completion.OriginalPending.Responder!=original.Responder || completion.OriginalPending.Response.Payload.Kind!=ResponsePayloadKind.Ownership ||
            fact.Owner.ProcessGeneration!=original.Responder.Generation)
            return KernelResult.Fail(KernelError.PlatformFaulted,"Original initial response ownership differs.");
        if(completion.OriginalInitialOwnership is { } prior)
            return ReferenceEquals(prior.Payload,fact.Payload) && prior.Region==fact.Region && prior.Owner==fact.Owner &&
                ReferenceEquals(prior.RegionOwner,fact.RegionOwner) && prior.Refusal is null ? KernelResult.Ok() :
                KernelResult.Fail(KernelError.PlatformFaulted,"Initial original response already refused; no repeat.");
        completion.OriginalInitialOwnership=fact;
        return KernelResult.Ok();
    }

    internal void RetainInitialRefusal(OriginalPublication original,KernelError error)
    {
        if(original.Completion is not CompletionRecord completion || !ReferenceEquals(original.Owner,this) ||
            !ReferenceEquals(original.Original,original) || !ReferenceEquals(completion.OriginalSelf,completion) ||
            completion.OriginalInitialOwnership is not { } fact || !ReferenceEquals(fact.Original,fact) ||
            completion.OwnershipTransferAttempted || fact.Refusal is not null || error==KernelError.None || !Enum.IsDefined(error))
            throw new InvalidOperationException("Initial response refusal continuity differs.");
        fact.Refusal=error;
    }

    internal KernelResult<InitialOwnership> ReadInitialRefusal(OriginalPublication original,object input)
    {
        if(!ReferenceEquals(original.Owner,this) || !ReferenceEquals(original.Original,original) || original.Completion is not CompletionRecord completion ||
            !ReferenceEquals(completion.OriginalSelf,completion) || completion.OriginalInitialOwnership is not { } fact ||
            !ReferenceEquals(fact.Original,fact) || !ReferenceEquals(fact.Payload,input) || fact.Refusal is not { } error ||
            !ReferenceEquals(fact.Publication.Original,fact.Publication) || !ReferenceEquals(fact.Publication.Owner,this) ||
            !ReferenceEquals(fact.Publication.Completion,completion) || fact.Publication.Responder!=original.Responder ||
            fact.Publication.Endpoint!=original.Endpoint || fact.Publication.Sequence!=original.Sequence ||
            fact.Charges.Length!=1 || fact.Charges[0].Owner!=original.Responder ||
            fact.Owner.ProcessGeneration!=original.Responder.Generation ||
            !ReferenceEquals(fact.Charges[0].RegionOwner,fact.RegionOwner) || fact.Payload.Handle!=fact.Region ||
            error==KernelError.None || !Enum.IsDefined(error) || completion.PreparedOwnership is not null || completion.OriginalTransferProducer is not null ||
            !ReadFailedTransferCompletion(original).IsSuccess && !ReadChannelCancellationReceipt(original).IsSuccess && (!TryResolveChannel(original.Endpoint.ChannelId,out ChannelRecord? channel) ||
                !TryResolveCompletion(channel,original.Sequence,out CompletionRecord? routed) || !ReferenceEquals(routed,completion) || completion.RawPublication is not null || completion.Source.Task.IsCompleted))
            return KernelResult<InitialOwnership>.Fail(KernelError.PlatformFaulted,"Original initial ownership refusal unavailable.");
        return KernelResult<InitialOwnership>.Ok(fact);
    }
    // Mutable Region/accounting truth remains in those authorities.
    internal sealed class TransferProducer
    {
        internal readonly TransferProducer Original;
        internal readonly OriginalPublication Publication;
        internal readonly PreparedOwnedTransfer Prepared;
        internal readonly RegionHandle SourceRegion;
        internal readonly DomainId SourceDomain;
        internal readonly RegionOwner TargetOwner;
        internal readonly object RegionOwner;
        internal readonly ProcessHandle Target;
        internal BudgetReservationHandle? TargetReservation;
        internal object? TargetBudgetOwner;
        internal bool TargetBudgetExpected;
        internal readonly bool SourceBudgetExpected;
        internal bool BudgetAdmissionKnown;
        internal KernelError? BudgetRefusal;
        internal KernelError? PreRegionRefusal;
        internal readonly (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation)[] SourceCharges;
        internal KernelResult<RegionHandle>? Raw, Composition;
        internal TransferProducer(OriginalPublication publication,PreparedOwnedTransfer prepared,RegionHandle sourceRegion,
            DomainId sourceDomain,RegionOwner targetOwner,object regionOwner,ProcessHandle target,
            BudgetReservationHandle? targetReservation,object? targetBudgetOwner,
            (object Fact,object? RegionOwner,ProcessHandle Owner,BudgetReservationHandle Reservation)[] sourceCharges)
        {
            Original=this;Publication=publication;Prepared=prepared;SourceRegion=sourceRegion;SourceDomain=sourceDomain;
            TargetOwner=targetOwner;RegionOwner=regionOwner;Target=target;TargetReservation=targetReservation;
            TargetBudgetOwner=targetBudgetOwner;SourceCharges=sourceCharges.ToArray();
            TargetBudgetExpected=targetReservation is not null;SourceBudgetExpected=sourceCharges.Length!=0;
        }
    }

    internal void RetainTransferProducer(TransferProducer producer)
    {
        OriginalPublication original =producer.Publication;
        if(!ReferenceEquals(producer.Original,producer) || !ReferenceEquals(original.Original,original) ||
            !ReferenceEquals(original.Owner,this) || original.Completion is not CompletionRecord completion ||
            !ReferenceEquals(completion.OriginalSelf,completion) || !ReferenceEquals(completion.PreparedOwnership,producer.Prepared) ||
            !ReferenceEquals(completion.OriginalOwnershipInput,producer.Prepared.Source) || completion.OriginalTransferProducer is not null ||
            completion.Requester!=producer.Target || producer.TargetReservation is not null && producer.TargetBudgetOwner is null)
            throw new InvalidOperationException("Original response transfer admission differs.");
        completion.OriginalTransferProducer=producer;
    }

    internal void RetainTransferRaw(OriginalPublication original,PreparedOwnedTransfer prepared,KernelResult<RegionHandle> raw,bool composition=false)
    {
        if(!ReferenceEquals(original.Original,original) || !ReferenceEquals(original.Owner,this) ||
            original.Completion is not CompletionRecord completion || !ReferenceEquals(completion.OriginalSelf,completion) ||
            completion.OriginalTransferProducer is not { } producer || !ReferenceEquals(producer.Original,producer) ||
            !ReferenceEquals(producer.Prepared,prepared) || !ReferenceEquals(completion.PreparedOwnership,prepared) ||
            (composition ? producer.Composition : producer.Raw) is not null)
            throw new InvalidOperationException("Original response transfer outcome differs.");
        if(composition)producer.Composition=raw;else producer.Raw=raw;
    }

    internal void RetainTransferBudget(OriginalPublication original,PreparedOwnedTransfer prepared,
        KernelResult<BudgetReservationHandle?> result,object? budgetOwner)
    {
        if(!ReferenceEquals(original.Original,original) || !ReferenceEquals(original.Owner,this) ||
            original.Completion is not CompletionRecord completion || !ReferenceEquals(completion.OriginalSelf,completion) ||
            completion.OriginalTransferProducer is not { } producer || !ReferenceEquals(producer.Original,producer) ||
            !ReferenceEquals(producer.Prepared,prepared) || producer.BudgetAdmissionKnown || producer.Raw is not null ||
            result.IsSuccess && result.Value is not null && budgetOwner is null)
            throw new InvalidOperationException("Original response budget admission differs.");
        producer.TargetReservation=result.IsSuccess ? result.Value : null;
        producer.TargetBudgetOwner=result.IsSuccess ? budgetOwner : null;
        producer.TargetBudgetExpected=producer.TargetReservation is not null;
        producer.BudgetRefusal=result.IsSuccess ? null : result.Error;
        producer.BudgetAdmissionKnown=true;
    }

    private static bool HasExactFailedTransferOutcome(TransferProducer producer)
    {
        if(!producer.BudgetAdmissionKnown)return false;
        if(producer.Composition is null || producer.Composition is { IsSuccess:true })
            return producer.BudgetRefusal is null && producer.PreRegionRefusal is null &&
                producer.Raw is { IsSuccess:true } retained && retained.Value.RegionId==producer.SourceRegion.RegionId &&
                retained.Value.Generation.Value==producer.SourceRegion.Generation.Value+1 &&
                (producer.Composition is null || producer.Composition.Value.Value==retained.Value) &&
                producer.Prepared.Projection is ITransferableOwnedPayload original && original.Handle==retained.Value;
        if(producer.Composition is not { IsSuccess:false } failed || !Enum.IsDefined(failed.Error))return false;
        if(producer.BudgetRefusal is { } refusal)
            return Enum.IsDefined(refusal) && refusal!=KernelError.None && failed.Error==refusal &&
                producer.Raw is null && producer.TargetReservation is null && producer.TargetBudgetOwner is null;
        if(producer.PreRegionRefusal is { } admissionRefusal)
            return admissionRefusal!=KernelError.None && Enum.IsDefined(admissionRefusal) &&
                failed.Error==admissionRefusal && producer.Raw is null;
        // Raw is retained directly from RegionAuthority.Transfer, whose checked
        // refusal precedes mutation. It proves only this operation's no-effect.
        if(producer.Raw is { IsSuccess:false } refused)
            return refused.Error!=KernelError.None && Enum.IsDefined(refused.Error) && failed.Error==refused.Error;
        return producer.Raw is { IsSuccess:true } raw && raw.Value.RegionId==producer.SourceRegion.RegionId &&
            raw.Value.Generation.Value==producer.SourceRegion.Generation.Value+1 &&
            producer.Prepared.Projection is ITransferableOwnedPayload projection && projection.Handle==raw.Value;
    }

    internal void RetainPreRegionRefusal(OriginalPublication original,PreparedOwnedTransfer prepared,KernelError error)
    {
        if(!ReferenceEquals(original.Original,original) || !ReferenceEquals(original.Owner,this) ||
            original.Completion is not CompletionRecord completion || !ReferenceEquals(completion.OriginalSelf,completion) ||
            completion.OriginalTransferProducer is not { } producer || !ReferenceEquals(producer.Original,producer) ||
            !ReferenceEquals(producer.Prepared,prepared) || !producer.BudgetAdmissionKnown || producer.BudgetRefusal is not null ||
            producer.PreRegionRefusal is not null || producer.Raw is not null || producer.Composition is not null ||
            error==KernelError.None || !Enum.IsDefined(error))
            throw new InvalidOperationException("Original pre-Region admission refusal differs.");
        producer.PreRegionRefusal=error;
    }

    internal KernelResult<TransferProducer> ReadFailedTransfer(OriginalPublication original,object input)
    {
        if(!ReferenceEquals(original.Original,original) || !ReferenceEquals(original.Owner,this) ||
            original.Completion is not CompletionRecord completion || !ReferenceEquals(completion.OriginalSelf,completion) ||
            completion.OriginalTransferProducer is not { } producer || !ReferenceEquals(producer.Original,producer) ||
            !ReferenceEquals(producer.Publication.Original,producer.Publication) || !ReferenceEquals(producer.Publication.Owner,this) ||
            !ReferenceEquals(producer.Publication.Completion,completion) || producer.Publication.Responder!=original.Responder ||
            producer.Publication.Endpoint!=original.Endpoint || producer.Publication.Sequence!=original.Sequence ||
            producer.Target!=completion.Requester || producer.TargetBudgetExpected!=(producer.TargetReservation is not null) ||
            producer.TargetBudgetExpected!=(producer.TargetBudgetOwner is not null) ||
            producer.SourceBudgetExpected!=(producer.SourceCharges.Length!=0) || producer.SourceCharges.Length>1 ||
            !ReferenceEquals(completion.OriginalOwnershipInput,input) || !ReferenceEquals(producer.Prepared.Source,input) ||
            !producer.Prepared.IsOriginal || !ReferenceEquals(completion.PreparedOwnership,producer.Prepared) ||
            !ReferenceEquals(completion.OwnershipProjection,producer.Prepared.Projection) || producer.Prepared.SourceHandle!=producer.SourceRegion ||
            !HasExactFailedTransferOutcome(producer) ||
            !ReadFailedTransferCompletion(original).IsSuccess && !ReadChannelCancellationReceipt(original).IsSuccess && (!TryResolveChannel(original.Endpoint.ChannelId,out ChannelRecord? channel) ||
                !TryResolveCompletion(channel,original.Sequence,out CompletionRecord? routed) || !ReferenceEquals(routed,completion) ||
                completion.RawPublication is not null || completion.Source.Task.IsCompleted))
            return KernelResult<TransferProducer>.Fail(KernelError.PlatformFaulted,"Original failed ownership response remains unresolved.");
        return KernelResult<TransferProducer>.Ok(producer);
    }

    internal void RetainTransferComposition(OriginalPublication original,PreparedOwnedTransfer prepared,KernelResult<RegionHandle> result)
    {
        if(original.Completion is CompletionRecord completion && completion.OriginalTransferProducer is not null)
            RetainTransferRaw(original,prepared,result,composition:true);
    }

    internal KernelResult<ResponseEnvelope> CompleteFailedTransfer(OriginalPublication original,object input)
    {
        KernelResult<ResponseEnvelope> terminal = ReadFailedTransferCompletion(original);
        if (terminal.IsSuccess)
            return original.Completion is CompletionRecord completed &&
                completed.OriginalFailedTransferCompletion is FailedTransferOutcome closedFact && ReferenceEquals(closedFact.Input, input)
                ? terminal : KernelResult<ResponseEnvelope>.Fail(KernelError.PlatformFaulted, "Original failed response input differs.");
        KernelResult<ResponseEnvelope> channelCancellation = ReadChannelCancellationReceipt(original);
        if (original.Completion is CompletionRecord uncertain &&
            (uncertain.OriginalFailedTransferCompletion is not null || uncertain.FailedTransferCompletion is not null ||
                !channelCancellation.IsSuccess && (uncertain.RawPublication is not null || uncertain.Source.Task.IsCompleted)))
            return KernelResult<ResponseEnvelope>.Fail(KernelError.PlatformFaulted, "Original failed response publication remains unknown; no repeat.");
        KernelResult<TransferProducer> known =ReadFailedTransfer(original,input);
        if(!known.IsSuccess && !ReadInitialRefusal(original,input).IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(known.Error,known.Message!);
        CompletionRecord completion =(CompletionRecord)original.Completion;
        if (channelCancellation.IsSuccess)
        {
            // Resource/charge cleanup already completed in the trusted caller.
            // Consume the existing independent channel publication, never publish again.
            FailedTransferOutcome closed = new(this, completion, original, input, channelCancellation.Value!);
            completion.OriginalFailedTransferCompletion = completion.FailedTransferCompletion = closed;
            completion.FailedTransferCleanupCompleted = true;
            return ReadFailedTransferCompletion(original);
        }
        if(!TryResolveChannel(original.Endpoint.ChannelId,out ChannelRecord? channel) ||
            !TryResolvePending(channel,original.Sequence,out PendingRecord? pending) || !ReferenceEquals(pending,completion.OriginalPending))
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotPending,"Original failed response pending route differs.");
        ResponseEnvelope cancelled =new ResponseEnvelope(original.Sequence,pending.Response.MessageId,ResponsePublicationStatus.Cancelled,null);
        FailedTransferOutcome outcome = new(this, completion, original, input, cancelled);
        completion.OriginalFailedTransferCompletion = completion.FailedTransferCompletion = outcome;
        Complete(channel,pending,cancelled);
        completion.FailedTransferCleanupCompleted=true;
        return ReadFailedTransferCompletion(original);
    }

    private sealed class FailedTransferOutcome
    {
        internal FailedTransferOutcome(ResponseRegistry owner, CompletionRecord completion, OriginalPublication request, object input, ResponseEnvelope raw)
        { Self = this; Owner = owner; Completion = completion; Request = request; Input = input; Raw = raw; }
        internal readonly FailedTransferOutcome Self;
        internal readonly ResponseRegistry Owner;
        internal readonly CompletionRecord Completion;
        internal readonly OriginalPublication Request;
        internal readonly object Input;
        internal readonly ResponseEnvelope Raw;
    }

    internal KernelResult<ResponseEnvelope> ReadFailedTransferCompletion(OriginalPublication original)
    {
        if (!ReferenceEquals(original.Owner, this) || !ReferenceEquals(original.Original, original) ||
            original.Completion is not CompletionRecord completion || !ReferenceEquals(completion.OriginalSelf, completion) ||
            completion.OriginalFailedTransferCompletion is not FailedTransferOutcome fact || !ReferenceEquals(fact.Self, fact) ||
            !ReferenceEquals(completion.FailedTransferCompletion, fact) || !ReferenceEquals(fact.Owner, this) ||
            !ReferenceEquals(fact.Completion, completion) || !ReferenceEquals(fact.Request.Original, fact.Request) ||
            !ReferenceEquals(fact.Request.Owner, this) || !ReferenceEquals(fact.Request.Completion, completion) ||
            fact.Request.Responder != original.Responder || fact.Request.Endpoint != original.Endpoint || fact.Request.Sequence != original.Sequence ||
            (!ReferenceEquals(completion.RawPublication, fact.Raw) && !ReferenceEquals(completion.OriginalChannelCancellation, fact.Raw)) ||
            fact.Raw.Status != ResponsePublicationStatus.Cancelled ||
            fact.Raw.RequestSequence != original.Sequence || fact.Raw.MessageId != completion.OriginalPending.Response.MessageId || fact.Raw.Payload is not null ||
            !completion.Source.Task.IsCompletedSuccessfully || !ReferenceEquals(completion.Source.Task.Result, fact.Raw))
            return KernelResult<ResponseEnvelope>.Fail(KernelError.PlatformFaulted, "Original failed response completion fact is absent or changed.");
        return KernelResult<ResponseEnvelope>.Ok(fact.Raw);
    }

    internal bool IsOriginalFailedTransferCompletion(OriginalPublication original,ResponseEnvelope cancelled)
    {
        KernelResult<ResponseEnvelope> terminal = ReadFailedTransferCompletion(original);
        return terminal.IsSuccess && ReferenceEquals(terminal.Value, cancelled);
    }
    internal KernelResult<OriginalPublication> CapturePublication(ProcessHandle responder,ChannelEndpointHandle endpoint,ulong sequence)
    {
        if(!TryResolveChannel(endpoint.ChannelId,out ChannelRecord? channel) || !TryResolveCompletion(channel,sequence,out CompletionRecord? completion) ||
            !ReferenceEquals(completion.OriginalSelf,completion) || completion.OriginalPending.Responder!=responder ||
            completion.OriginalPending.ResponderEndpoint!=endpoint.EndpointId)
            return KernelResult<OriginalPublication>.Fail(KernelError.ResponseNotPending,"Original response completion absent/foreign.");
        return KernelResult<OriginalPublication>.Ok(new(this,completion,responder,endpoint,sequence));
    }
    internal bool ReadConsumedPublication(OriginalPublication original,ResponseEnvelope raw)
    {
        if(!ReferenceEquals(original.Owner,this) || !ReferenceEquals(original.Original,original) ||
            original.Completion is not CompletionRecord completion || !ReferenceEquals(completion.OriginalSelf,completion) ||
            !ReferenceEquals(Volatile.Read(ref completion.Consumed),raw))return false;
        return completion.OriginalPending.Responder==original.Responder && completion.OriginalPending.ResponderEndpoint==original.Endpoint.EndpointId &&
            raw.RequestSequence==original.Sequence && raw.MessageId==completion.OriginalPending.Response.MessageId &&
            completion.Source.Task.IsCompletedSuccessfully && ReferenceEquals(completion.Source.Task.Result,raw) &&
            HasOriginalPublishedPayload(completion,raw) &&
            (ReferenceEquals(completion.RawPublication,raw) || ReferenceEquals(completion.OriginalChannelCancellation,raw));
    }
    internal KernelResult<ResponseEnvelope> ReadPublication(OriginalPublication original)
    {
        if(!ReferenceEquals(original.Owner,this) || !ReferenceEquals(original.Original,original) || original.Completion is not CompletionRecord completion ||
            !ReferenceEquals(completion.OriginalSelf,completion) || !completion.PublicationCommitted || completion.RawPublication is not { } raw ||
            completion.OriginalPending.Responder!=original.Responder || completion.OriginalPending.ResponderEndpoint!=original.Endpoint.EndpointId ||
            raw.RequestSequence!=original.Sequence || raw.MessageId!=completion.OriginalPending.Response.MessageId || raw.Status!=ResponsePublicationStatus.Published ||
            !completion.Source.Task.IsCompletedSuccessfully || !ReferenceEquals(completion.Source.Task.Result,raw) ||
            !HasOriginalPublishedPayload(completion,raw))
            return KernelResult<ResponseEnvelope>.Fail(KernelError.PlatformFaulted,"Exact original committed response unavailable; no repeat.");
        return KernelResult<ResponseEnvelope>.Ok(raw); // Metadata only; survives legitimate routing retirement.
    }

    internal KernelResult<ResponseEnvelope> ReadOriginalCancellation(OriginalPublication original)
    {
        if(!ReferenceEquals(original.Owner,this) || !ReferenceEquals(original.Original,original) ||
            original.Completion is not CompletionRecord completion || !ReferenceEquals(completion.OriginalSelf,completion) ||
            completion.OwnershipTransferAttempted || completion.OriginalPending.Responder!=original.Responder ||
            completion.OriginalPending.ResponderEndpoint!=original.Endpoint.EndpointId ||
            !completion.PublicationCommitted || completion.RawPublication is not { Status:ResponsePublicationStatus.Cancelled } raw ||
            raw.RequestSequence!=original.Sequence || raw.MessageId!=completion.OriginalPending.Response.MessageId ||
            !completion.Source.Task.IsCompletedSuccessfully || !ReferenceEquals(completion.Source.Task.Result,raw))
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotPending,"Exact original response cancellation unavailable.");
        return KernelResult<ResponseEnvelope>.Ok(raw);
    }

    internal KernelResult<ResponseEnvelope> ReadOriginalChannelCancellation(OriginalPublication original)
    {
        if (original.Completion is not CompletionRecord completion || completion.OwnershipTransferAttempted)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotPending, "Original ownership cleanup is independent of channel cancellation.");
        return ReadChannelCancellationReceipt(original);
    }

    // A channel receipt is publication evidence only. Ownership cleanup requires
    // the separately retained original producer/input/Region/quantitative facts.
    private KernelResult<ResponseEnvelope> ReadChannelCancellationReceipt(OriginalPublication original)
    {
        if(!ReferenceEquals(original.Owner,this) || !ReferenceEquals(original.Original,original) ||
            original.Completion is not CompletionRecord completion || !ReferenceEquals(completion.OriginalSelf,completion) ||
            completion.OriginalPending.Responder!=original.Responder ||
            completion.OriginalPending.ResponderEndpoint!=original.Endpoint.EndpointId ||
            completion.OriginalChannelCancellation is not { Status:ResponsePublicationStatus.Cancelled } cancelled ||
            cancelled.RequestSequence!=original.Sequence || cancelled.MessageId!=completion.OriginalPending.Response.MessageId ||
            cancelled.Payload is not null || !completion.Source.Task.IsCompletedSuccessfully ||
            !ReferenceEquals(completion.Source.Task.Result,cancelled))
            return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotPending,"Exact original channel cancellation unavailable.");
        return KernelResult<ResponseEnvelope>.Ok(cancelled);
    }

    internal KernelResult<object> ReadFailedOwnershipInput(OriginalPublication original)
    {
        if (original.Completion is CompletionRecord completion)
        {
            object? input = completion.OriginalTransferProducer?.Prepared.Source ?? completion.OriginalInitialOwnership?.Payload;
            if (input is not null && (ReadFailedTransfer(original, input).IsSuccess || ReadInitialRefusal(original, input).IsSuccess))
                return KernelResult<object>.Ok(input);
        }
        return KernelResult<object>.Fail(KernelError.PlatformFaulted, "Exact original failed ownership input remains unavailable.");
    }

    internal bool HasRetainedOwnershipResponse(OriginalPublication original) =>
        ReferenceEquals(original.Owner, this) && ReferenceEquals(original.Original, original) &&
        original.Completion is CompletionRecord completion && ReferenceEquals(completion.OriginalSelf, completion) &&
        (completion.OriginalTransferProducer is not null || completion.PreparedOwnership is not null ||
            completion.OriginalInitialOwnership is not null || completion.OriginalFailedTransferCompletion is not null);
    internal bool MatchesOriginalPublicationPayload(OriginalPublication original,object input,ResponseEnvelope outcome)
    {
        KernelResult<ResponseEnvelope> known =ReadPublication(original);
        if(!known.IsSuccess || !ReferenceEquals(known.Value,outcome) || original.Completion is not CompletionRecord completion)return false;
        return ReferenceEquals(input,outcome.Payload) || completion.OwnershipTransferAttempted &&
            ReferenceEquals(completion.OriginalOwnershipInput,input) && completion.PreparedOwnership is { } prepared &&
            ReferenceEquals(prepared.Source,input) && ReferenceEquals(prepared.Projection,completion.OwnershipProjection) &&
            ReferenceEquals(completion.OwnershipProjection,outcome.Payload);
    }

    private void CloseChannel(ChannelId channelId)
    {
        if (!_ownedChannels.TryGetValue(channelId, out ChannelRecord? channel) || !OwnsOriginalChannel(channelId,channel) || channel.Closed) return;
        channel.Closed=true;
        if (_channels.TryGetValue(channelId, out ChannelRecord? routed) && ReferenceEquals(routed, channel))
            _channels.Remove(channelId);

        foreach (CompletionRecord? completion in channel.OwnedCompletions.Values.OrderBy(static completion => completion.OriginalPending.RequestSequence))
        {
            PendingRecord pending = completion.OriginalPending;
            ResponseEnvelope cancelled =new ResponseEnvelope(
                pending.RequestSequence,
                pending.Response.MessageId,
                ResponsePublicationStatus.Cancelled,
                null);
            if(!completion.Source.Task.IsCompleted)
            {
                completion.OriginalChannelCancellation=cancelled;
                _=completion.Source.TrySetResult(cancelled);
            }
        }

        channel.Pending.Clear();
        // Ownership is already requester-owned. A registered waiter still needs
        // its original completion until delivery or an independently proved reclaim.
        HashSet<ulong> retained =channel.OwnedCompletions.Where(pair=>pair.Value.Consumed is null && pair.Value.OwnershipTransferAttempted &&
            pair.Value.OriginalPending.Response.Payload.Kind==ResponsePayloadKind.Ownership &&
            pair.Value.PublicationCommitted && pair.Value.RawPublication is { Status:ResponsePublicationStatus.Published })
            .Select(pair=>pair.Key).ToHashSet();
        foreach(Queue<ResponseEnvelope>? queue in new[]{channel.LeftResponses,channel.RightResponses})
        {
            int count =queue.Count;
            for(int i =0;i<count;i++){ ResponseEnvelope raw =queue.Dequeue();if(retained.Contains(raw.RequestSequence))queue.Enqueue(raw);}
        }
        foreach(ulong key in channel.OwnedCompletions.Keys.Where(key=>!retained.Contains(key)).ToArray())
        {channel.Completions.Remove(key);channel.OwnedCompletions.Remove(key);}
        if(retained.Count==0)_ownedChannels.Remove(channelId);
    }

    internal void RetireReclaimedClosedResponses(ProcessHandle requester, DomainId? settledDomain = null)
    {
        foreach(KeyValuePair<ChannelId, ChannelRecord> pair in _ownedChannels.Where(pair=>pair.Value.Closed).ToArray())
        {
            ChannelRecord channel =pair.Value;
            if(!OwnsOriginalChannel(pair.Key,channel))continue;
            foreach(KeyValuePair<ulong, CompletionRecord> item in channel.OwnedCompletions.ToArray())
            {
                CompletionRecord completion =item.Value;
                if(!ReferenceEquals(completion.OriginalSelf,completion) || !ReferenceEquals(completion.OriginalChannelIdentity,channel.Identity) ||
                    completion.OriginalPublishedRegionOwner is not { } owner || completion.OriginalPublishedRegion is not { } region ||
                    !completion.Source.Task.IsCompletedSuccessfully || !ReferenceEquals(completion.Source.Task.Result,completion.RawPublication) ||
                    !HasOriginalPublishedPayload(completion,completion.Source.Task.Result) ||
                    completion.RawPublication?.Payload is not ITransferableOwnedPayload payload || !payload.IsOriginalForRuntime || payload.Handle!=region ||
                    !_regions.HasReleasedOriginalRegion(owner,region) ||
                    completion.Requester!=requester && (settledDomain is null || !_regions.HasReleasedOriginalRegion(owner,region,settledDomain)))continue;
                RemoveQueuedResponse(ResponseQueue(channel,completion.RequesterEndpoint),item.Key);
                channel.Completions.Remove(item.Key);channel.OwnedCompletions.Remove(item.Key);
            }
            if(channel.OwnedCompletions.Count==0)_ownedChannels.Remove(pair.Key);
        }
    }

    private static bool HasOriginalPublishedPayload(CompletionRecord completion,ResponseEnvelope raw)
    {
        if(raw.Status!=ResponsePublicationStatus.Published || completion.OriginalPending.Response.Payload.Kind!=ResponsePayloadKind.Ownership)return true;
        return completion.OwnershipTransferAttempted && completion.PreparedOwnership is { IsOriginal:true } prepared &&
            completion.OriginalOwnershipInput is ITransferableOwnedPayload source && source.IsOriginalForRuntime &&
            ReferenceEquals(prepared.Source,source) && ReferenceEquals(prepared.Projection,completion.OwnershipProjection) &&
            raw.Payload is ITransferableOwnedPayload projection && projection.IsOriginalForRuntime &&
            ReferenceEquals(prepared.Projection,projection) && completion.OriginalPublishedRegion==projection.Handle;
    }

    private static bool TryResolveCompletion(ChannelRecord channel, ulong sequence, out CompletionRecord completion)
    {
        if (channel.Completions.TryGetValue(sequence, out CompletionRecord? routed) &&
            channel.OwnedCompletions.TryGetValue(sequence, out CompletionRecord? original) && ReferenceEquals(routed, original) &&
            ReferenceEquals(original.OriginalSelf,original) && ReferenceEquals(original.OriginalChannelIdentity,channel.Identity))
        {
            completion = original;
            return true;
        }
        completion = null!;
        return false;
    }

    private static bool TryResolvePending(ChannelRecord channel, ulong sequence, out PendingRecord pending)
    {
        if (TryResolveCompletion(channel, sequence, out CompletionRecord? completion) && !completion.Source.Task.IsCompleted &&
            channel.Pending.TryGetValue(sequence, out PendingRecord? routed) && ReferenceEquals(routed, completion.OriginalPending))
        {
            pending = routed;
            return true;
        }
        pending = null!;
        return false;
    }

    private bool TryResolveChannel(ChannelId id, out ChannelRecord channel)
    {
        if (_channels.TryGetValue(id, out ChannelRecord? routed) && _ownedChannels.TryGetValue(id, out ChannelRecord? original) &&
            ReferenceEquals(routed, original) && OwnsOriginalChannel(id,original))
        {
            channel = original;
            return true;
        }
        channel = null!;
        return false;
    }
    private bool TryResolveClosedChannel(ChannelEndpointHandle endpoint,out ChannelRecord channel)
    {
        if(endpoint.EndpointId.Value is 1 or 2 && _ownedChannels.TryGetValue(endpoint.ChannelId,out ChannelRecord? original) &&
            OwnsOriginalChannel(endpoint.ChannelId,original) && original.Closed && original.OriginalGeneration==endpoint.Generation &&
            _requestChannels.SnapshotClosure(endpoint) is { Closed:true })
        {channel=original;return true;}
        channel=null!;return false;
    }
    private bool OwnsOriginalChannel(ChannelId id,ChannelRecord channel) => ReferenceEquals(channel.Owner,this) &&
        ReferenceEquals(channel.OriginalSelf,channel) && channel.OriginalChannel==id;
    private static Queue<ResponseEnvelope> ResponseQueue(ChannelRecord channel, EndpointId endpoint) =>
        endpoint.Value == 1 ? channel.LeftResponses : channel.RightResponses;

    private static void RemoveQueuedResponse(Queue<ResponseEnvelope> queue, ulong requestSequence)
    {
        int count = queue.Count;
        bool removed = false;
        for (int index = 0; index < count; index++)
        {
            ResponseEnvelope envelope = queue.Dequeue();
            if (!removed && envelope.RequestSequence == requestSequence)
            {
                removed = true;
                continue;
            }
            queue.Enqueue(envelope);
        }
    }

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
                string primitiveTypeName = payload.GetType().FullName ?? payload.GetType().Name;
                return string.Equals(primitiveTypeName, expected.TypeName, StringComparison.Ordinal)
                    ? KernelResult.Ok()
                    : KernelResult.Fail(KernelError.UnsupportedPayload, $"Expected primitive response type {expected.TypeName}, got {primitiveTypeName}.");

            case ResponsePayloadKind.Enum:
                if (payload is not Enum) return KernelResult.Fail(KernelError.UnsupportedPayload, "Response requires the declared enum payload.");
                string enumTypeName = payload.GetType().FullName ?? payload.GetType().Name;
                return string.Equals(enumTypeName, expected.TypeName, StringComparison.Ordinal)
                    ? KernelResult.Ok()
                    : KernelResult.Fail(KernelError.UnsupportedPayload, $"Expected enum response type {expected.TypeName}, got {enumTypeName}.");

            case ResponsePayloadKind.Bounded:
                if (payload is not IBoundedPayload bounded)
                    return KernelResult.Fail(KernelError.UnsupportedPayload, "Response requires the declared bounded payload.");
                string boundedTypeName = payload.GetType().FullName ?? payload.GetType().Name;
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


